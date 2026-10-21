"""Camera adapters for seven attributed upstream denoisers. MIT for adapter code.

Upstream files/weights retain their own licenses (ASwin: CC BY-NC 4.0).
Outputs identify the genuine capture: ASwin uses its latest-frame prediction;
other temporal models use the centre. No retimestamping.
"""
import argparse
import importlib.util
from pathlib import Path
import sys
import types
import gc
import ast
import torch
import torch.nn.functional as F

NAMES = {2: 'FastDVDnet', 3: 'ASwin', 4: 'ViDeNN', 5: 'FlowDenoise · RAFT Small', 6: 'UDVD', 7:'ShiftNet Small',8:'PVDD sRGB'}
COUNTS = {2: 5, 3: 6, 4: 3, 5: 5, 6: 5,7:5,8:5}

def clear_package(name):
    for key in list(sys.modules):
        if key==name or key.startswith(name+'.'):del sys.modules[key]

def pvdd_network(path):
    # Limit imports to the supplied PVDD inference graph. Its generic package
    # initializers also import unrelated architectures and training extensions.
    for name in ('models','utils'):clear_package(name)
    for name,relative in [('models','models'),('models.networks','models/networks'),('models.modules','models/modules'),('models.utils','models/utils'),('utils','utils')]:
        package=types.ModuleType(name);package.__path__=[str(path/relative)];sys.modules[name]=package
    from models.networks.base import BaseNet
    from models.utils.util import make_layer
    # ResUNet is taken verbatim through its AST, excluding unrelated VSR imports.
    scope={'torch':torch,'nn':torch.nn,'F':F,'BaseNet':BaseNet,'make_layer':make_layer}
    classes=ast.parse((path/'models/networks/pvdd0815_net.py').read_text(encoding='utf8'))
    node=next(n for n in classes.body if isinstance(n,ast.ClassDef) and n.name=='ResidualBlockNoBN')
    exec(compile(ast.Module(body=[node],type_ignores=[]),'<pvdd residual block>','exec'),scope)
    tree=ast.parse((path/'models/networks/realbasicvsr_net.py').read_text(encoding='utf8'))
    nodes=[n for n in tree.body if isinstance(n,(ast.ClassDef,ast.FunctionDef)) and n.name in ('ResUNet','conv_block','up_block')]
    exec(compile(ast.Module(body=nodes,type_ignores=[]),'<pvdd ResUNet>','exec'),scope)
    shim=types.ModuleType('models.networks.realbasicvsr_net');shim.ResUNet=scope['ResUNet'];sys.modules[shim.__name__]=shim
    from models.networks.pvdd0815_net import pvdd0815
    return pvdd0815(num_feat=64,num_block=3,num_block_f=3,num_block_pre=3,dynamic_refine_thres=255.,is_sequential_cleaning=False,depth=2,depth_pre=1,num_head=8,num_frames=2,window_size=[8,8],window_size_pre=[16,16],mlp_ratio=2.,qkv_bias=True,qk_scale=None,drop_rate=0.,attn_drop_rate=0,drop_path_rate=0.,drop_path=0.,mlp='04')

def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    result = importlib.util.module_from_spec(spec)
    sys.modules[name] = result
    spec.loader.exec_module(result)
    return result

class ViDeNNNet(torch.nn.Module):
    """Exact inference architecture translated from clausmichele/ViDeNN (MIT).
    Kernels are converted from official TF checkpoints; no random weights used.
    """
    def __init__(self):
        super().__init__()
        self.spatial = torch.nn.ModuleList()
        self.temporal = torch.nn.ModuleList()
        for i in range(1, 21):
            self.spatial.append(torch.nn.Conv2d(3 if i == 1 else 128 if i == 2 else 64,
                128 if i == 1 else 3 if i == 20 else 64, 3, padding=1, bias=i == 1))
            self.temporal.append(torch.nn.Conv2d(9 if i == 1 else 128 if i == 2 else 64,
                128 if i == 1 else 3 if i == 20 else 64, 3, padding=1, bias=i == 1))
        self.norms = torch.nn.ModuleList([torch.nn.BatchNorm2d(64, eps=.001) for _ in range(18)])

    def forward(self, frames):
        # The upstream inference reads OpenCV BGR frames.
        x = frames[:, [2, 1, 0]]
        residual = x
        for i, layer in enumerate(self.spatial):
            residual = layer(residual)
            if i == 0: residual = F.relu(residual)
            elif i < 19: residual = F.relu(self.norms[i-1](residual))
        spatial = x-residual
        residual = spatial.reshape(1, 9, *x.shape[-2:])
        for i, layer in enumerate(self.temporal):
            residual = layer(residual)
            if i < 19: residual = F.leaky_relu(residual, .2)
        return (spatial[1:2]-residual)[:, [2, 1, 0]]

class Adapter:
    def __init__(self, root, backend):
        self.root = Path(root)
        self.backend = backend
        self.device = torch.device('cuda' if torch.cuda.is_available() else 'cpu')
        self.model = None
        if backend==7 and self.device.type!='cuda':raise RuntimeError('This upstream ShiftNet implementation requires NVIDIA CUDA.')
        self.graph=None
        self.graph_inputs=None
        self.graph_output=None
        self.graph_shape=None
        if backend == 2:
            path=self.root/'fastdvdnet'
            self.model=module('camera_fastdvdnet',path/'models.py').FastDVDnet()
            weights=torch.load(path/'model_clipped_noise.pth',map_location='cpu',weights_only=True)
            self.model.load_state_dict({k.removeprefix('module.'):v for k,v in weights.items()})
        elif backend == 3:
            path=self.root/'aswin'
            self.model=module('camera_aswin',path/'network.py').Net(6,in_nc=3,dim=64)
            self.model.load_state_dict(torch.load(path/'weights.pth',map_location='cpu',weights_only=True))
        elif backend == 4:
            self.model=ViDeNNNet()
            self.model.load_state_dict(torch.load(self.root/'videnn'/'converted.pth',map_location='cpu',weights_only=True))
        elif backend == 5:
            # ComfyUI is only used for progress display by the upstream helpers.
            comfy=types.ModuleType('comfy'); comfy.utils=types.ModuleType('comfy.utils')
            sys.modules['comfy']=comfy;sys.modules['comfy.utils']=comfy.utils
            self.flow=module('camera_flowdenoise',self.root/'flowdenoise'/'nodes.py')
            from torchvision.models.optical_flow import raft_small, Raft_Small_Weights
            weights=Raft_Small_Weights.DEFAULT
            self.model=raft_small(weights=None)
            self.model.load_state_dict(torch.load(self.root/'flowdenoise'/'raft_small.pth',map_location='cpu',weights_only=True))
            self.transform=weights.transforms()
        elif backend == 6:
            path=self.root/'udvd'
            clear_package('models')
            sys.path.insert(0,str(path))
            import models
            self.model=models.MODEL_REGISTRY['blind-video-net-4']()
            self.model.load_state_dict(torch.load(path/'weights.pth',map_location='cpu',weights_only=True))
            self.post=module('camera_udvd_post',path/'post_processing.py').post_process
        elif backend==7:
            path=self.root/'shiftnet'
            self.model=module('camera_shiftnet',path/'network.py').GShiftNet(future_frames=2,past_frames=2)
            self.model.load_state_dict(torch.load(path/'weights.pth',map_location='cpu',weights_only=True)['params'])
        elif backend==8:
            path=self.root/'pvdd'
            self.model=pvdd_network(path)
            self.model.load_state_dict(torch.load(path/'weights.pth',map_location='cpu',weights_only=True))
        else: raise ValueError('Unknown model')
        self.model.eval().to(self.device)
        if backend==3 and self.device.type=='cuda': self.model.half()

    @torch.inference_mode()
    def run(self, frames, level):
        if frames.shape[0] != COUNTS[self.backend]: raise ValueError('Wrong temporal window')
        frames=frames.to(self.device)
        h,w=frames.shape[-2:];center=frames.shape[0]-1 if self.backend==3 else frames.shape[0]-2 if self.backend==5 and level<4 else frames.shape[0]//2
        # Noise controls are sigma for non-blind models, output blend for blind ones.
        sigma=(12,20,32,48)[level-1]/255.
        amp=self.device.type=='cuda'
        with torch.autocast(self.device.type, dtype=torch.float16, enabled=amp and self.backend!=5):
            if self.backend==2:
                x=F.pad(frames,(0,(-w)%4,0,(-h)%4),mode='reflect')
                noise=torch.full((1,1,*x.shape[-2:]),sigma,device=self.device)
                result=self.model(x.reshape(1,15,*x.shape[-2:]),noise)[...,:h,:w]
            elif self.backend==3:
                # The supplied attention implementation retains fixed spatial
                # position encodings: its coarsest map must be at least 16x16.
                padded=F.pad(frames,(0,max(0,128-w),0,max(0,128-h)),mode='reflect')
                x=padded.permute(1,0,2,3).unsqueeze(0)
                result=self.graph_model(x.half() if amp else x)[:,:,center,:h,:w]
            elif self.backend==4: result=self.model(frames)
            elif self.backend==6:
                output,_=self.model(frames.reshape(1,15,h,w))
                # Same posterior calculation as the upstream demo, in float32.
                with torch.autocast(self.device.type,enabled=False):
                    result,_=self.post(output.float(),frames[center:center+1].float(),model='blind-video-net',sigma=sigma,device=self.device)
            elif self.backend==7:
                x=F.pad(frames,(0,(-w)%32,0,(-h)%32),mode='reflect').unsqueeze(0)
                noise=torch.full((1,5,1,*x.shape[-2:]),sigma,device=self.device)
                result=self.graph_model(x,noise)[...,:h,:w]
            elif self.backend==8:
                x=F.pad(frames,(0,(-w)%16,0,(-h)%16),mode='reflect').unsqueeze(0)
                result=self.model(x)[0,center:center+1,...,:h,:w]
            else: result=self.flow_center(frames,center,level)
        result=result.float()
        if self.backend in (3,4,8): result=torch.lerp(frames[center:center+1],result,(.35,.55,.8,1.)[level-1])
        if not torch.isfinite(result).all(): raise RuntimeError('Model returned non-finite pixels')
        return result.clamp(0,1),center

    def graph_model(self,*inputs):
        # Replays the identical trained graph with fresh input storage; no
        # tensor-to-CPU operations or data-dependent branches are captured.
        if self.device.type!='cuda':return self.model(*inputs)
        shape=tuple((tuple(x.shape),x.dtype) for x in inputs)
        if self.graph_shape!=shape:
            self.graph=None;self.graph_output=None;self.graph_inputs=None
            stream=torch.cuda.Stream();stream.wait_stream(torch.cuda.current_stream())
            with torch.cuda.stream(stream):
                for _ in range(3):self.model(*inputs)
            torch.cuda.current_stream().wait_stream(stream)
            self.graph_inputs=tuple(x.clone() for x in inputs)
            self.graph=torch.cuda.CUDAGraph()
            with torch.cuda.graph(self.graph):self.graph_output=self.model(*self.graph_inputs)
            self.graph_shape=shape
        for target,source in zip(self.graph_inputs,inputs):target.copy_(source)
        self.graph.replay()
        return self.graph_output

    def flow_center(self,frames,center,level):
        # Adapted from FlowDenoise TemporalFlowAverage._denoise_raft (MIT).
        # Only the requested centre is evaluated; the original computes all B.
        # Use torchvision's official normalization rather than 0..255 inputs.
        h,w=frames.shape[-2:]
        padded=F.pad(frames,(0,(-w)%8,0,(-h)%8),mode='reflect')
        # Stabilize flow estimation under sensor grain; averaging still uses
        # unblurred neighbouring frames. Original thresholds target weaker noise.
        flow_input=F.avg_pool2d(F.pad(padded,(2,2,2,2),mode='reflect'),5,stride=1)
        reference=frames[center:center+1];total=reference.clone();weight=torch.ones_like(reference[:,:1])
        for i in range(len(frames)):
            if i==center:continue
            # Low/Medium/High use the adjacent three-frame window to leave
            # latency headroom. Ultra uses all five frames for more averaging.
            if level<4 and abs(i-center)>1:continue
            neighbour=frames[i:i+1]
            if (neighbour-reference).abs().mean().item()>.15:continue
            a,b=self.transform(flow_input[center:center+1],flow_input[i:i+1])
            forward=self.model(a,b,num_flow_updates=6)[-1][...,:h,:w].float()
            backward=self.model(b,a,num_flow_updates=6)[-1][...,:h,:w].float()
            warped=self.flow._warp_with_flow(neighbour,forward)
            mask=self.flow._color_similarity_mask(warped,reference,(.04,.08,.12,.18)[level-1])
            mask*=self.flow._forward_backward_occlusion_mask(forward,backward)
            temporal=.8**abs(i-center)
            total+=temporal*mask*warped;weight+=temporal*mask
        clean=(total/(weight+1e-8)).permute(0,2,3,1)
        strength=(.35,.55,.8,1.)[level-1]
        return self.flow.SelectiveDenoise().denoise(reference.permute(0,2,3,1),clean,strength,strength,'YCbCr',True)[0].permute(0,3,1,2)

    def close(self):
        self.graph=None;self.graph_inputs=None;self.graph_output=None;self.graph_shape=None
        self.model=None
        gc.collect()
        if torch.cuda.is_available():torch.cuda.empty_cache()
