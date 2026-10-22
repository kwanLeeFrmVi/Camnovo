"""Local, same-input comparison; camera pixels never leave this process.

Camera reference is a separate temporal mean, not clean ground truth. A known
chart and moving chart check whether noise reduction simply removes texture.
Offline timing only nominates profiles; live output must qualify separately.
"""
import argparse,ctypes as C,json,time,sys,gc,winreg
from pathlib import Path
import numpy as np
import cv2
from PIL import Image,ImageDraw,ImageFont
import torch
import torch.nn.functional as F
sys.path.insert(0,str(Path(__file__).resolve().parent))
from model_adapters import Adapter,COUNTS,NAMES

W,H=1920,1080
def encode(rgb):
    x=rgb.astype(np.float32)/255;r,g,b=x[...,0],x[...,1],x[...,2]
    y=.2126*r+.7152*g+.0722*b
    u=cv2.resize((b-y)/1.8556,(W//2,H//2),interpolation=cv2.INTER_AREA)
    v=cv2.resize((r-y)/1.5748,(W//2,H//2),interpolation=cv2.INTER_AREA)
    yy=np.clip(np.rint(y*219+16),16,235).astype('uint8')
    uv=np.clip(np.rint(np.stack((u,v),-1)*224+128),16,240).astype('uint8')
    return np.concatenate((yy.ravel(),uv.ravel()))
def decode(packed):
    y=(packed[:W*H].reshape(H,W).astype('float32')-16)/219
    uv=(packed[W*H:].reshape(H//2,W//2,2).astype('float32')-128)/224
    uv=cv2.resize(uv,(W,H),interpolation=cv2.INTER_LINEAR);u,v=uv[...,0],uv[...,1]
    return np.clip(np.stack((y+1.5748*v,y-.187324*u-.468124*v,y+1.8556*u),-1),0,1)
def capture(path):
    dll=C.CDLL(str(path));dll.PreviewStartSized.argtypes=[C.c_int,C.c_int]
    dll.PreviewInfo.argtypes=[C.POINTER(C.c_int),C.POINTER(C.c_int),C.POINTER(C.c_ulonglong)]
    dll.PreviewCopy.argtypes=[C.c_void_p,C.c_int,C.c_int,C.c_int,C.POINTER(C.c_ulonglong)]
    key=winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,r'SOFTWARE\LenovoWebcamPrototype',0,winreg.KEY_READ|winreg.KEY_SET_VALUE|winreg.KEY_WOW64_64KEY)
    names=('DenoiseLevel','ShadowCleanupEnabled','FlipFlags');old={n:winreg.QueryValueEx(key,n)[0] for n in names}
    try:
        for n in names:winreg.SetValueEx(key,n,0,winreg.REG_DWORD,0)
        hr=dll.PreviewStartSized(W,H)
        if hr<0:raise RuntimeError(f'Camera start 0x{hr&0xffffffff:08x}')
        frame=np.empty((H,W,4),'uint8');serial=C.c_ulonglong();start=time.monotonic();ref=[];frames=[];seen=0
        while len(frames)<12:
            if time.monotonic()-start>20:raise TimeoutError('Camera sample timed out')
            w,h,seq=C.c_int(),C.c_int(),C.c_ulonglong();hr=dll.PreviewInfo(C.byref(w),C.byref(h),C.byref(seq))
            if hr<0:raise RuntimeError(f'Camera read 0x{hr&0xffffffff:08x}')
            if w.value==W and h.value==H and seq.value!=serial.value:
                hr=dll.PreviewCopy(frame.ctypes.data,W,H,W*4,C.byref(serial))
                if hr!=0:continue
                seen+=1
                if seen<=35:continue
                rgb=frame[...,2::-1].copy()
                if len(ref)<48:ref.append(rgb)
                else:frames.append(rgb)
            else:time.sleep(.003)
        return np.asarray(ref),np.asarray(frames)
    finally:
        dll.CameraClose()
        for n,value in old.items():winreg.SetValueEx(key,n,0,winreg.REG_DWORD,value)
        key.Close()
def chart():
    rng=np.random.default_rng(7721);x=np.full((H,W,3),92,'uint8')
    for yy in range(0,H,180):
        for xx in range(0,W,240):
            shade=45+(xx//240+yy//180)%5*27;x[yy:yy+180,xx:xx+240]=shade
    image=Image.fromarray(x);draw=ImageDraw.Draw(image)
    font=ImageFont.truetype('C:/Windows/Fonts/segoeui.ttf',22)
    for y in range(32,400,44):draw.text((40,y),'CAMNOVO 1080p · fine text 0123456789',font=font,fill=(205,200,190))
    for period in (2,4,6,8,12):
        left=700+(period in (6,8,12))*450;top=30+((2,4,6,8,12).index(period))*145
        for x0 in range(left,left+360,period*2):draw.rectangle((x0,top,x0+period-1,top+96),fill=(175,165,150))
    x=np.array(image)
    texture=rng.normal(0,1,(320,900)).astype('float32');texture=cv2.GaussianBlur(texture,(0,0),.8);texture/=texture.std()
    x[650:970,30:930]=np.clip(108+texture[...,None]*17,0,255)
    x[650:970,1000:1880]=50
    return x
def luma(x):return x@np.array([.2126,.7152,.0722],np.float32)
def high(x):return x-cv2.GaussianBlur(x,(0,0),1.2)
def mse(a,b,mask):return float(np.mean((a[mask]-b[mask])**2))
def main():
    p=argparse.ArgumentParser();p.add_argument('--camera',required=True);p.add_argument('--native',required=True);p.add_argument('--models',required=True);p.add_argument('--output',required=True);p.add_argument('--only',type=int,default=-1);p.add_argument('--widths',default='');p.add_argument('--profiles-json');a=p.parse_args()
    outdir=Path(a.output);outdir.mkdir(parents=True,exist_ok=True)
    torch.set_num_threads(4);torch.backends.cudnn.benchmark=True;torch.manual_seed(7721)
    print('Capturing a short local sample; camera pixels are not saved.',flush=True)
    ref,raw=capture(a.camera);reference=ref.astype('float32').mean(0)/255
    drift=np.max(np.abs(ref[:24].astype('float32').mean(0)-ref[24:].astype('float32').mean(0)),axis=2)
    stable=cv2.GaussianBlur(drift,(0,0),3)<8
    stable[:24]=False;stable[-24:]=False;stable[:,:24]=False;stable[:,-24:]=False
    lum=luma(reference);guide=cv2.GaussianBlur(lum,(0,0),1.5);grad=np.sqrt(cv2.Sobel(guide,cv2.CV_32F,1,0)**2+cv2.Sobel(guide,cv2.CV_32F,0,1)**2)
    flat=stable&(grad<.06)&(lum<.6)&(lum>.03)
    print('Camera diagnostics '+json.dumps({'lumaMin':float(lum.min()),'lumaMedian':float(np.median(lum)),'lumaMax':float(lum.max()),'stablePixels':int(stable.sum()),'flatPixels':int(flat.sum()),'medianDrift255':float(np.median(drift))}),flush=True)
    if flat.sum()<15000:raise RuntimeError('Not enough stable, untextured camera pixels for comparison. Check shutter and lighting.')
    # Independent reference vs test window; exclude locations that moved between them.
    testmean=raw.astype('float32').mean(0)/255
    still=cv2.GaussianBlur(np.max(np.abs(testmean-reference),axis=2),(0,0),3)<.035
    flat &= still
    if flat.sum()<15000:raise RuntimeError('Scene moved between reference and evaluation; keep camera scene still.')
    rawerror=float(np.mean([(mse(f.astype('float32')/255,reference,flat)) for f in raw]))
    sigma=float(np.clip(np.sqrt(rawerror)*255,8,30));rng=np.random.default_rng(7721)
    clean=chart();cleanref=decode(encode(clean));cleanlum=luma(cleanref);hp=high(cleanlum)
    detailmask=np.abs(hp)>.025;flatmask=np.zeros((H,W),bool);flatmask[690:930,1040:1840]=True
    static=[np.clip(clean.astype('float32')+rng.normal(0,sigma,clean.shape),0,255).astype('uint8') for _ in range(6)]
    moving=[];movingclean=[]
    for i in range(6):
        shifted=np.roll(clean,(i-3)*7,axis=1);movingclean.append(decode(encode(shifted)));moving.append(np.clip(shifted.astype('float32')+rng.normal(0,sigma,clean.shape),0,255).astype('uint8'))
    native=C.CDLL(str(Path(a.native).resolve()));native.QualityCreate.argtypes=[C.c_uint,C.c_uint,C.POINTER(C.c_void_p)];native.QualityRun.argtypes=[C.c_void_p,C.c_uint,C.c_uint,C.c_void_p,C.c_void_p];native.QualityDestroy.argtypes=[C.c_void_p]
    results=[]
    config={0:[1920],1:[1920],2:[640,960,1280,1920],3:[192,320],4:[640,960,1280],5:[320,640,960],6:[320,640],7:[192,320],8:[320,640]}
    metadata={'date':'2026-10-22','gpu':torch.cuda.get_device_name(),'cameraReference':'48-frame temporal mean; separate 12-frame test sample, stable pixels only','stableFlatPixels':int(flat.sum()),'cameraRawRMSE255':float(np.sqrt(rawerror)*255),'chartNoiseSigma255':sigma,'detailMetric':'Known-chart high-frequency contrast response; 1 preserves reference detail','latency':'Offline nomination only; live P95 and delivered-frame checks required','results':results}
    def save(): (outdir/'quality-results.json').write_text(json.dumps(metadata,indent=2),encoding='utf8')
    if a.only>=0:config={a.only:[int(w) for w in a.widths.split(',')] if a.widths else config[a.only]}
    profiles=json.loads(Path(a.profiles_json).read_text()) if a.profiles_json else None
    if profiles:config={b:sorted({p['width'] for p in profiles if p['backend']==b}) for b in sorted({p['backend'] for p in profiles})}
    for backend,widths in config.items():
        adapter=Adapter(a.models,backend) if backend>=2 else None
        for width in widths:
            for level in (1,2,3):
                if profiles and not any(p['backend']==backend and p['width']==width and p['level']==level for p in profiles):continue
                entry={'backend':backend,'name':NAMES.get(backend,('NVIDIA RTX' if backend==0 else 'Bilateral GPU')),'width':width,'level':level}
                context=C.c_void_p()
                try:
                    if backend<2:
                        hr=native.QualityCreate(W,H,C.byref(context))
                        if hr<0:raise RuntimeError(f'Native init 0x{hr&0xffffffff:08x}')
                    def run(frames):
                        packed=[encode(f) for f in frames];begin=time.perf_counter()
                        if backend<2:
                            output=np.empty(W*H*3//2,'uint8')
                            for source in packed:
                                hr=native.QualityRun(context,backend,level,source.ctypes.data,output.ctypes.data)
                                if hr<0:raise RuntimeError(f'Native processing 0x{hr&0xffffffff:08x}')
                            result=decode(output);center=len(frames)-1
                            return result,center,(time.perf_counter()-begin)*1000/len(frames)
                        count=COUNTS[backend];packed=packed[-count:]
                        device=adapter.device;source=torch.from_numpy(np.stack(packed)).to(device)
                        yy=source[:,:W*H].reshape(count,1,H,W).float();uv=source[:,W*H:].reshape(count,H//2,W//2,2).permute(0,3,1,2).float()
                        th=max(128 if backend==5 else 8,round(H*width/W/8)*8)
                        yy=F.interpolate(yy,size=(th,width),mode='bilinear',align_corners=False);uv=F.interpolate(uv,size=(th,width),mode='bilinear',align_corners=False)-128
                        y=(yy-16)/219;u=uv[:,0:1]/224;v=uv[:,1:2]/224
                        rgb=torch.cat((y+1.5748*v,y-.187324*u-.468124*v,y+1.8556*u),1).clamp(0,1)
                        with torch.inference_mode():output,center=adapter.run(rgb,level);output=F.interpolate(output,size=(H,W),mode='bilinear',align_corners=False).clamp(0,1)
                        result=output[0].permute(1,2,0).cpu().numpy();elapsed=(time.perf_counter()-begin)*1000
                        return decode(encode(np.rint(result*255).astype('uint8'))),len(frames)-count+center,elapsed
                    # Warm and then measure identical samples for every candidate.
                    run(static);test,center,ms=run(static)
                    cameraoutputs=[];cameraerrors=[];times=[ms]
                    for offset in (0,3,6):
                        output,c,t=run(list(raw[offset:offset+6]));cameraoutputs.append(output);cameraerrors.append(mse(output,reference,flat));times.append(t)
                    moved,c,t=run(moving);times.append(t)
                    response=float(np.sum(high(luma(test))[detailmask]*hp[detailmask])/np.sum(hp[detailmask]**2))
                    chartmse=mse(test,cleanref,detailmask);movingmse=mse(moved,movingclean[c],detailmask)
                    cameraRatio=float(np.mean(cameraerrors)/rawerror)
                    temporal=float(np.mean(np.var(np.stack([o[flat] for o in cameraoutputs]),axis=0)))
                    sigmaout=float(np.sqrt(mse(test,cleanref,flatmask))*255)
                    entry.update(cameraErrorRatio=cameraRatio,cameraRMSEChangePercent=100*(1-np.sqrt(cameraRatio)),chartDetailResponse=response,chartDetailPSNR=10*np.log10(1/max(chartmse,1e-10)),motionDetailPSNR=10*np.log10(1/max(movingmse,1e-10)),chartFlatRMSE255=sigmaout,temporalResidual255=float(np.sqrt(temporal)*255),warmMs=float(np.median(times)),maxWarmMs=max(times),cold=False)
                    rawflat=np.sqrt(mse(decode(encode(static[center])),cleanref,flatmask))*255
                    entry['chartNoiseReductionPercent']=float(100*(1-sigmaout/rawflat))
                    # High-pass residual removes exposure/lighting drift from
                    # the camera grain estimate. Camera absolute RMSE above is
                    # retained separately and must not be labelled pure noise.
                    rawgrain=float(np.mean([mse(high(f.astype('float32')/255),high(reference),flat) for f in raw]))
                    filteredgrain=float(np.mean([mse(high(o),high(reference),flat) for o in cameraoutputs]))
                    entry['cameraGrainReductionPercent']=float(100*(1-np.sqrt(filteredgrain/rawgrain)))
                    # A detail-heavy blend. No-response/pass-through does not win
                    # simply for preserving texture. Actual latency is a separate gate.
                    noiseBenefit=np.clip(entry['chartNoiseReductionPercent']/100,-1,1)
                    cameraBenefit=np.clip(entry['cameraGrainReductionPercent']/100,-1,1)
                    entry['qualityScore']=float(50*np.clip(response,0,1)+15*noiseBenefit+15*cameraBenefit+20*np.clip((entry['motionDetailPSNR']-18)/18,0,1))
                    future=(0 if backend<2 or backend==3 else 1 if backend in (4,5) else 2)*33.34
                    entry['estimatedAgeMs']=entry['maxWarmMs']+future+120
                    entry['nominate']=entry['estimatedAgeMs']<480
                    # Only known synthetic textures are saved for visual checks.
                    crop=np.rint(test[28:390,28:670]*255).astype('uint8');Image.fromarray(crop).save(outdir/f'b{backend}-w{width}-l{level}.png')
                    print(json.dumps(entry),flush=True)
                except Exception as error:entry['error']=str(error);print('ERROR '+str(entry),flush=True)
                finally:
                    if context.value:native.QualityDestroy(context)
                    results.append(entry);save()
            # Larger sizes of an already late profile cannot be selected live;
            # still record tested profiles rather than inventing measurements.
        if adapter:adapter.close();del adapter;gc.collect();torch.cuda.empty_cache()
    Image.fromarray(clean[28:390,28:670]).save(outdir/'chart-clean.png');Image.fromarray(static[3][28:390,28:670]).save(outdir/'chart-noisy.png')
    save();print('Completed; no camera images saved.',flush=True)
if __name__=='__main__':main()
