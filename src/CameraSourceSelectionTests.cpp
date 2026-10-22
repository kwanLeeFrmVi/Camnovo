#include "camera_source_selection.h"
#include <vector>
#include <cstdio>
#include <stdexcept>
static std::wstring Select(const std::vector<std::wstring>& paths){std::wstring choice;int best=0;for(auto& path:paths){int rank=CamnovoSourceRank(path);if(rank>best){best=rank;choice=path;}}return choice;}
int main(){try{
    std::wstring prefix=L"\\\\?\\USB#VID_17EF&PID_483C&MI_00#INSTANCE#";
    std::wstring camera=prefix+L"{e5323777-f976-4f5b-9b55-b94699c46e44}\\GLOBAL";
    std::wstring legacy=prefix+L"{65e8773d-8f56-11d0-a3b9-00a0c9223196}\\GLOBAL";
    std::wstring ir=L"\\\\?\\USB#VID_17EF&PID_483C&MI_02#INSTANCE#{e5323777-f976-4f5b-9b55-b94699c46e44}\\GLOBAL";
    if(Select({camera,legacy,ir})!=camera||Select({legacy,ir,camera})!=camera||Select({ir,legacy,camera,legacy})!=camera)throw std::runtime_error("Enumeration order selected a path without Camnovo.");
    if(Select({ir,legacy})!=legacy||!Select({ir,L"unrelated webcam"}).empty())throw std::runtime_error("Fallback/IR selection is wrong.");
    std::wstring lower=camera;for(auto& c:lower)c=towlower(c);if(CamnovoSourceRank(lower)!=2)throw std::runtime_error("Case-sensitive path ranking.");
    std::puts("PASS: camera processing route preferred in all enumeration orders; legacy-only fallback, case variants and IR exclusion verified.");
}catch(const std::exception& error){std::printf("FAIL: %s\n",error.what());return 1;}return 0;}
