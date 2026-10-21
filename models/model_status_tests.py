"""Regression: a locked diagnostics file must not fail video processing."""
import argparse
import ctypes as C
from ctypes import wintypes as W
from pathlib import Path
import sys
import json
import os
sys.path.insert(0,str(Path(__file__).resolve().parent))
from model_worker import write_status
p=argparse.ArgumentParser();p.add_argument('--folder',required=True);a=p.parse_args()
path=Path(a.folder).resolve()/'locked-model-status-test.json'
path.write_text('{"state":"old"}',encoding='utf8')
k=C.WinDLL('kernel32',use_last_error=True)
k.CreateFileW.argtypes=[W.LPCWSTR,W.DWORD,W.DWORD,C.c_void_p,W.DWORD,W.DWORD,W.HANDLE];k.CreateFileW.restype=W.HANDLE
k.CloseHandle.argtypes=[W.HANDLE]
handle=k.CreateFileW(str(path),0x80000000,1,None,3,0,None)
assert handle!=C.c_void_p(-1).value
try:
    write_status(path,{'state':'new'})
    assert json.loads(path.read_text())['state']=='old'
finally:k.CloseHandle(handle)
write_status(path,{'state':'new'})
assert json.loads(path.read_text())['state']=='new'
path.unlink();tmp=path.with_suffix(f'.{os.getpid()}.tmp')
if tmp.exists():tmp.unlink()
print('PASS: locked Windows status file is non-fatal; last valid JSON retained; updates resume after unlock. No video protocol operation is involved in diagnostics.')
