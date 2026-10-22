#pragma once
#include <string>
#include <cwctype>

// The same RGB hardware can be enumerated under both the camera and legacy
// capture categories. Only the camera category has our Device MFT attachment.
// Enumeration order changes after service reconnects; never select the last
// matching VID/PID without considering its interface category.
inline int CamnovoSourceRank(std::wstring link) {
    for(auto& c:link)c=static_cast<wchar_t>(towlower(c));
    if(link.find(L"vid_17ef&pid_483c&mi_00#")==std::wstring::npos)return 0;
    return link.find(L"#{e5323777-f976-4f5b-9b55-b94699c46e44}")!=std::wstring::npos?2:1;
}
