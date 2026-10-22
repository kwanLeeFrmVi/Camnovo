"""Local-only camera inference pipe. Never writes or uploads camera frames."""
import argparse
import ctypes as C
from ctypes import wintypes as W
import json
import os
from pathlib import Path
import struct
import sys
import time
import traceback
import re
import winreg
sys.path.insert(0,str(Path(__file__).resolve().parent))

def write_status(path,data):
    # Diagnostics must never invalidate an already-written video response.
    # Windows readers/antivirus can briefly deny replacement of this file.
    try:
        tmp=path.with_suffix(f'.{os.getpid()}.tmp')
        tmp.write_text(json.dumps(data),encoding='utf8')
        os.replace(tmp,path)
    except OSError:pass

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--pipe',required=True);parser.add_argument('--sid',required=True)
    parser.add_argument('--models',required=True);parser.add_argument('--status',required=True)
    args=parser.parse_args()
    if not re.fullmatch('[0-9a-f]{32}',args.pipe) or not re.fullmatch(r'S-1-[0-9-]+',args.sid):raise ValueError('Invalid pipe identity')
    status_path=Path(args.status);status_path.parent.mkdir(parents=True,exist_ok=True)
    diagnostics={}
    def status(state,backend=-1,message='',milliseconds=0):
        data={'state':state,'backend':backend,'message':message,'milliseconds':milliseconds,'pid':os.getpid(),'time':time.time()}
        data.update(diagnostics)
        write_status(status_path,data)
    status('starting',message='Starting model runtime…')
    import numpy as np
    import torch
    import torch.nn.functional as F
    from model_adapters import Adapter,COUNTS,NAMES
    from live_policy import LiveSizePolicy
    size_policy=LiveSizePolicy()
    def automatic_size():
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,r'SOFTWARE\LenovoWebcamPrototype',0,winreg.KEY_READ|winreg.KEY_WOW64_64KEY) as settings:
                return winreg.QueryValueEx(settings,'DenoiseAutoSize')[0]!=0
        except OSError:return True
    torch.set_num_threads(4)
    torch.backends.cudnn.benchmark=True
    kernel=C.WinDLL('kernel32',use_last_error=True);advapi=C.WinDLL('advapi32',use_last_error=True)
    media_clock=C.WinDLL('mfplat').MFGetSystemTime;media_clock.restype=C.c_longlong
    class Security(C.Structure):_fields_=[('length',W.DWORD),('descriptor',C.c_void_p),('inherit',W.BOOL)]
    descriptor=C.c_void_p()
    advapi.ConvertStringSecurityDescriptorToSecurityDescriptorW.argtypes=[W.LPCWSTR,W.DWORD,C.POINTER(C.c_void_p),C.c_void_p]
    sddl=f'D:P(A;;GA;;;{args.sid})(A;;GA;;;LS)(A;;GA;;;NS)(A;;GA;;;SY)(A;;GA;;;BA)'
    if not advapi.ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl,1,C.byref(descriptor),None):raise C.WinError(C.get_last_error())
    security=Security(C.sizeof(Security),descriptor,False)
    kernel.CreateNamedPipeW.argtypes=[W.LPCWSTR,W.DWORD,W.DWORD,W.DWORD,W.DWORD,W.DWORD,W.DWORD,C.POINTER(Security)]
    kernel.CreateNamedPipeW.restype=W.HANDLE
    kernel.ConnectNamedPipe.argtypes=[W.HANDLE,C.c_void_p]
    kernel.DisconnectNamedPipe.argtypes=[W.HANDLE]
    kernel.ReadFile.argtypes=[W.HANDLE,C.c_void_p,W.DWORD,C.POINTER(W.DWORD),C.c_void_p]
    kernel.WriteFile.argtypes=kernel.ReadFile.argtypes
    kernel.CloseHandle.argtypes=[W.HANDLE]
    handle=kernel.CreateNamedPipeW(r'\\.\pipe\LenovoCameraStudio-Denoise-'+args.pipe,3|0x80000,8,1,65536,65536,1000,C.byref(security))
    if handle==C.c_void_p(-1).value:raise C.WinError(C.get_last_error())
    def read(n):
        data=bytearray(n);buffer=(C.c_ubyte*n).from_buffer(data);offset=0
        while offset<n:
            done=W.DWORD()
            if not kernel.ReadFile(handle,C.byref(buffer,offset),n-offset,C.byref(done),None) or not done.value:raise BrokenPipeError()
            offset+=done.value
        return data
    def write(data):
        buffer=C.create_string_buffer(data);offset=0
        while offset<len(data):
            done=W.DWORD()
            if not kernel.WriteFile(handle,C.byref(buffer,offset),len(data)-offset,C.byref(done),None) or not done.value:raise BrokenPipeError()
            offset+=done.value
    adapter=None
    last_backend=None
    status('ready',message='Model runtime ready')
    try:
        while True:
            if not kernel.ConnectNamedPipe(handle,None) and C.get_last_error()!=535:raise C.WinError(C.get_last_error())
            try:
                while True:
                    magic,version,backend,level,w,h,count,size,model_width,flags=struct.unpack('<10I',read(40))
                    if magic!=0x3144434c or version!=1 or backend not in COUNTS or count!=COUNTS[backend] or not 1<=level<=4 or w%2 or h%2 or not 2<=w<=2560 or not 2<=h<=1440 or size!=w*h*3//2 or model_width not in (128,192,320,640,960,1280,1920):raise ValueError('Invalid camera request')
                    timestamps=[];buffers=[]
                    if backend!=last_backend:diagnostics.clear();last_backend=backend
                    for _ in range(count):
                        timestamps.append(struct.unpack('<q',read(8))[0]);buffers.append(read(size))
                    effective_width=size_policy.choose(backend,level,model_width,automatic_size())
                    diagnostics.update(modelWidth=effective_width,requestedWidth=model_width,automaticSize=size_policy.key[3],level=level,sourceAgeMs=(media_clock()-timestamps[-1])/10000.,captureIntervalMs=(timestamps[-1]-timestamps[0])/10000./max(1,count-1))
                    try:
                        if adapter is None or adapter.backend!=backend:
                            if adapter is not None:adapter.close();adapter=None
                            status('loading',backend,'Loading '+NAMES[backend]+'…')
                            adapter=Adapter(args.models,backend)
                        status('processing',backend,NAMES[backend])
                        begin=time.perf_counter();device=adapter.device
                        # NV12 matrix/range are supplied by the camera media type.
                        packed=torch.from_numpy(np.stack([np.frombuffer(b,dtype=np.uint8) for b in buffers])).to(device)
                        yy=packed[:,:w*h].reshape(count,1,h,w).float()
                        uv=packed[:,w*h:].reshape(count,h//2,w//2,2).permute(0,3,1,2).float()
                        tw=min(w,effective_width);th=max(128 if backend==5 else 8,round(h*tw/w/8)*8)
                        yy=F.interpolate(yy,size=(th,tw),mode='bilinear',align_corners=False)
                        uv=F.interpolate(uv,size=(th,tw),mode='bilinear',align_corners=False)-128
                        full=bool(flags&2);matrix601=bool(flags&1)
                        y=yy/255 if full else (yy-16)/219
                        u=uv[:,0:1]/(255 if full else 224);v=uv[:,1:2]/(255 if full else 224)
                        kr,kb=(.299,.114) if matrix601 else (.2126,.0722);kg=1-kr-kb
                        rgb=torch.cat((y+2*(1-kr)*v,y-2*kb*(1-kb)/kg*u-2*kr*(1-kr)/kg*v,y+2*(1-kb)*u),1).clamp(0,1)
                        result,center=adapter.run(rgb,level)
                        result=F.interpolate(result,size=(h,w),mode='bilinear',align_corners=False).clamp(0,1)
                        r,g,b=result[:,0:1],result[:,1:2],result[:,2:3]
                        y=kr*r+kg*g+kb*b
                        u=F.avg_pool2d((b-y)/(2*(1-kb)),2);v=F.avg_pool2d((r-y)/(2*(1-kr)),2)
                        yy=(y*(255 if full else 219)+(0 if full else 16)).round().clamp(0 if full else 16,255 if full else 235).byte()
                        uv=torch.stack((u,v),-1).mul(255 if full else 224).add(128).round().clamp(0 if full else 16,255 if full else 240).byte()
                        output=torch.cat((yy.flatten(),uv.flatten())).cpu().numpy().tobytes()
                        milliseconds=(time.perf_counter()-begin)*1000
                        diagnostics.update(outputAgeMs=(media_clock()-timestamps[center])/10000.,inferenceMs=milliseconds)
                        diagnostics.pop('lastError',None)
                        size_policy.observe(diagnostics['outputAgeMs'])
                        write(struct.pack('<4Iqd',magic,1,0,size,timestamps[center],milliseconds)+output)
                        status('active',backend,NAMES[backend],milliseconds)
                    except BrokenPipeError:raise
                    except Exception as error:
                        diagnostics['lastError']=str(error)
                        traceback.print_exc();status('error',backend,str(error))
                        write(struct.pack('<4Iqd',magic,1,0x80004005,0,0,0))
                        time.sleep(.25)
            except (BrokenPipeError,OSError):pass
            except Exception as error:traceback.print_exc();status('error',message=str(error))
            finally:kernel.DisconnectNamedPipe(handle)
    finally:kernel.CloseHandle(handle)

if __name__=='__main__':main()
