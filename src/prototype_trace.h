#pragma once
void PrototypeInitialize(HRESULT result, DWORD inputs, DWORD outputs);
void PrototypeOutput(DWORD stream, LONG64 count, HRESULT orientationResult, HRESULT nvidiaResult);
void PrototypeShutdownTrace();
void PrototypeOperation(const char* operation, HRESULT result, DWORD stream = 0);
