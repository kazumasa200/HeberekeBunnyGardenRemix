// Copied unchanged from Remix Plus (https://github.com/RemixProjGroup/dxvk-remix, MIT), dxgi/dxgi_forward.cpp at 35a98479.
// dxgi.dll forwarder to the unified module. See src/d3d11/d3d11_forward.cpp
// for why the implementation lives in d3d9.dll and why these are /export
// pragmas rather than a .def file.

#pragma comment(linker, "/export:CreateDXGIFactory=d3d9.CreateDXGIFactory")
#pragma comment(linker, "/export:CreateDXGIFactory1=d3d9.CreateDXGIFactory1")
#pragma comment(linker, "/export:CreateDXGIFactory2=d3d9.CreateDXGIFactory2")
#pragma comment(linker, "/export:DXGIDeclareAdapterRemovalSupport=d3d9.DXGIDeclareAdapterRemovalSupport")
#pragma comment(linker, "/export:DXGIGetDebugInterface1=d3d9.DXGIGetDebugInterface1")

extern "C" int _fltused = 0;
