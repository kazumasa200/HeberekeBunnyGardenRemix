// Copied unchanged from Remix Plus (https://github.com/RemixProjGroup/dxvk-remix, MIT), d3d11/d3d11_forward.cpp at 35a98479.
// d3d11.dll forwarder.
//
// The real D3D11 implementation is compiled into the unified module
// (d3d9.dll) alongside the D3D9 frontend and libdxvk. Two DLLs that each
// statically link libdxvk get a private copy of every global in it -- their
// own RtxOptions, their own SceneManager, their own DxvkInstance -- so they
// could never share a DxvkDevice, and sharing one is the entire point: the
// game's D3D11 resources have to be Vulkan resources on the device Remix
// path traces with.
//
// Games look for a file named d3d11.dll next to the executable, so this
// stands in for one. Every export below is a true forwarder: Windows
// resolves it at load time directly to d3d9.dll, with no thunk and no code
// of ours on the call path.
//
// Done with /export pragmas rather than a .def, because MSVC resolves
// "name = module.name" in a .def as an alias to a LOCAL symbol and fails the
// link with LNK2001 when it cannot find one.

#pragma comment(linker, "/export:D3D11CoreCreateDevice=d3d9.D3D11CoreCreateDevice")
#pragma comment(linker, "/export:D3D11CreateDevice=d3d9.D3D11CreateDevice")
#pragma comment(linker, "/export:D3D11CreateDeviceAndSwapChain=d3d9.D3D11CreateDeviceAndSwapChain")
#pragma comment(linker, "/export:D3D11On12CreateDevice=d3d9.D3D11On12CreateDevice")

extern "C" int _fltused = 0;
