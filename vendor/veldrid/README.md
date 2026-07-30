# Veldrid Immediate-Context Fork

These packages come from `APKiwiOrg/veldrid` commit `bb15b063b53923fa7cb31e5ef5380a7966369d6f`, tag `v4.9.100`.

They are based on upstream Veldrid `v4.9.0` and retain Vortice `2.3.0`. The fork adds the opt-in
`D3D11DeviceOptions.UseImmediateContext` mode used by `KhaozEngine.Gpu` for Direct3D11 only. The matching
Windows Veldrid suite covers buffer, render, resource-set, and texture paths through this mode.

Package version: `4.9.100`

SHA-256:

```text
08db02a6fb731692a38a97d64106b1cb733aae5ac7c3edcaa497eead4e25d47d  Veldrid.4.9.100.nupkg
74f359b363c494c9dda98a56d02ff324cb65e7a3c494dc28423aeb25269eff2d  Veldrid.MetalBindings.4.9.100.nupkg
4d0033996c7b3de4624a0336a35149a8417a2ecebc3900e370173210e0b6149f  Veldrid.OpenGLBindings.4.9.100.nupkg
```