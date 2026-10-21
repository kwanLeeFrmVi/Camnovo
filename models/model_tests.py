"""Runs actual pretrained models on controlled synthetic noisy video, no camera."""
import argparse
from pathlib import Path
import sys
import time
import json
import torch
sys.path.insert(0,str(Path(__file__).resolve().parent))
from model_adapters import Adapter,COUNTS,NAMES

p=argparse.ArgumentParser();p.add_argument('--models',required=True);p.add_argument('--report',required=True);p.add_argument('--width',type=int,default=320);p.add_argument('--only',type=int,default=0);p.add_argument('--profiles',action='store_true');a=p.parse_args()
torch.set_num_threads(4);torch.manual_seed(341);torch.backends.cudnn.benchmark=True
results=[]
for backend in ([a.only] if a.only else COUNTS):
    try:
        adapter=Adapter(a.models,backend);w=({2:640,3:192,4:640,5:320,6:320,7:192,8:320}[backend] if a.profiles else a.width);h=max(128 if backend==5 else 32,round(w*9/16/8)*8)
        count=COUNTS[backend];clean=torch.full((count,3,h,w),.4,device=adapter.device)
        # Flat regions and a sharp boundary distinguish denoising from raw pass-through.
        clean[:,:,:,w//2:]=.65
        noisy=(clean+torch.randn_like(clean)*(30/255)).clamp(0,1)
        expected=count-1 if backend==3 else count-2 if backend==5 else count//2
        before=(noisy[expected]-clean[expected]).square().mean().item()*255**2
        begin=time.perf_counter();out,center=adapter.run(noisy,3)
        if adapter.device.type=='cuda':torch.cuda.synchronize()
        cold=(time.perf_counter()-begin)*1000
        timings=[]
        for _ in range(3):
            begin=time.perf_counter();out,center=adapter.run(noisy,3)
            if adapter.device.type=='cuda':torch.cuda.synchronize()
            timings.append((time.perf_counter()-begin)*1000)
        after=(out[0]-clean[center]).square().mean().item()*255**2
        assert out.shape==(1,3,h,w) and center==expected
        assert torch.isfinite(out).all() and after<before*.95,(before,after)
        entry={'backend':backend,'name':NAMES[backend],'width':w,'height':h,'device':str(adapter.device),'beforeMSE':before,'afterMSE':after,'coldMs':cold,'warmMs':sum(timings)/len(timings),'pass':True}
        if backend in (3,7) and adapter.device.type=='cuda':
            changed=(noisy+.06).clamp(0,1);graph_model=adapter.graph_model
            try:
                adapter.graph_model=lambda *inputs:adapter.model(*inputs)
                eager,_=adapter.run(changed,3)
            finally:adapter.graph_model=graph_model
            replay,_=adapter.run(changed,3)
            error=(eager-replay).abs().max().item()
            assert error<.002,error
            assert (replay-out).abs().mean().item()>.001,'Graph reused old frame pixels'
            entry['freshInputGraphMaxError']=error
        print('PASS: '+json.dumps(entry),flush=True);results.append(entry);adapter.close();del adapter
    except Exception as error:
        import traceback;traceback.print_exc();results.append({'backend':backend,'name':NAMES[backend],'pass':False,'error':str(error)})
    Path(a.report).write_text(json.dumps(results,indent=2),encoding='utf8')
if any(not r['pass'] for r in results):sys.exit(1)
