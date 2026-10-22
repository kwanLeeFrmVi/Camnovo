// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Lenovo Camera Studio contributors
#pragma once
#include <algorithm>
#include <cmath>
#include <cstddef>

// Suppress chroma in deep shadows without smoothing across colour edges.
// The brightest luma in a chroma footprint protects adjoining bright detail.
// No temporal state, look-ahead, or source-buffer mutation is involved.
inline void CleanupShadowColour(BYTE* image,LONG stride,UINT width,UINT height,
    GUID format,bool fullRange,DWORD strength) {
    if(!strength)return;
    unsigned blend[256];
    const int black=fullRange?0:16,range=fullRange?255:219;
    for(int i=0;i<256;++i){
        double light=std::clamp((i-black)/double(range),0.0,1.0);
        double shadow=std::clamp((80.0/255.0-light)/(72.0/255.0),0.0,1.0);
        blend[i]=unsigned(std::lround(256.0*strength/100.0*shadow*shadow*(3.0-2.0*shadow)));
    }
    auto neutralize=[](BYTE value,unsigned amount){return BYTE((unsigned(value)*(256-amount)+128*amount+128)>>8);};
    if(format==MFVideoFormat_NV12){
        for(UINT y=0;y<height;y+=2){
            const BYTE* a=image+ptrdiff_t(y)*stride;const BYTE* b=a+stride;
            BYTE* uv=image+ptrdiff_t(height+y/2)*stride;
            for(UINT x=0;x<width;x+=2){
                unsigned light=(std::max)((std::max)(a[x],a[x+1]),(std::max)(b[x],b[x+1]));
                unsigned amount=blend[light];uv[x]=neutralize(uv[x],amount);uv[x+1]=neutralize(uv[x+1],amount);
            }
        }
    }else if(format==MFVideoFormat_YUY2||format==MFVideoFormat_UYVY){
        unsigned luma=format==MFVideoFormat_UYVY?1:0;
        for(UINT y=0;y<height;++y){BYTE* row=image+ptrdiff_t(y)*stride;
            for(UINT x=0;x<width*2;x+=4){unsigned amount=blend[(std::max)(row[x+luma],row[x+2+luma])];row[x+1-luma]=neutralize(row[x+1-luma],amount);row[x+3-luma]=neutralize(row[x+3-luma],amount);}
        }
    }else{
        unsigned bytes=format==MFVideoFormat_RGB24?3:4;
        for(UINT y=0;y<height;++y){BYTE* row=image+ptrdiff_t(y)*stride;
            for(UINT x=0;x<width;++x){BYTE* pixel=row+x*bytes;
                unsigned grey=(54u*pixel[2]+183u*pixel[1]+19u*pixel[0]+128)>>8;
                int encoded=fullRange?int(grey):16+int((grey*219+127)/255);
                unsigned amount=blend[encoded];
                for(unsigned c=0;c<3;++c)pixel[c]=BYTE((unsigned(pixel[c])*(256-amount)+grey*amount+128)>>8);
            }
        }
    }
}
