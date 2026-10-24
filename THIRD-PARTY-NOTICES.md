# Third-party notices

ImmersiveSBS is licensed under the GNU General Public License v3.0 (see [LICENSE](LICENSE)). It
ports code from [DesktopSbS](https://github.com/Harry908/DesktopSbS) by the same author, also
GPL-3.0. It includes or uses the following software and models, each under its own licence.

## Shipped with the app

| Component | Version | Licence | Copyright |
|---|---|---|---|
| [.NET runtime, WPF and Windows Forms](https://github.com/dotnet/runtime) (self-contained package) | 10.0 | MIT | .NET Foundation and Contributors |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) (`Microsoft.ML.OnnxRuntime`, `.Managed`) | 1.30.0 | MIT | Microsoft Corporation |
| Windows ML (`Microsoft.Windows.AI.MachineLearning`: its projection and `Microsoft.Windows.AI.MachineLearning.dll` only, to install TensorRT for RTX) | 2.4.89 | Microsoft Software License Terms (Windows ML Runtime; `licenses\windowsml-license.txt`) | Microsoft Corporation |
| [System.Numerics.Tensors](https://github.com/dotnet/runtime) | 9.0.0 | MIT | Microsoft Corporation |
| [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (`Vortice.Direct3D11`, `.D3DCompiler`, `.DXGI`, `.DirectX`) | 3.8.3 | MIT | Amer Koleci and Contributors |
| [Vortice.Mathematics](https://github.com/amerkoleci/Vortice.Mathematics) | 2.1.0 | MIT | Amer Koleci and Contributors |
| [SharpGen.Runtime](https://github.com/SharpGenTools/SharpGenTools) (and `.COM`) | 2.4.2-beta | MIT | 2010-2017 Alexandre Mutel, 2017-2023 Jeremy Koritzinsky, 2023-2024 Amer Koleci |
| [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) (in the installer, `driver\`; signed by SignPath Foundation) | 25.7.23 | MIT | 2024 Virtual Display |
| `devcon.exe` (in the installer, `driver\`; from the driver's package, signed by Microsoft) | | Windows Driver Kit tool; source under MS-PL in [Windows-driver-samples](https://github.com/microsoft/Windows-driver-samples) | Microsoft Corporation |

The installer is built with [Inno Setup](https://jrsoftware.org/isinfo.php) (Inno Setup License,
Jordan Russell and Martijn Laan). The driver's own licence is in `driver\LICENSE-VirtualDisplayDriver.txt`.

A package also carries ONNX Runtime's own `LICENSE` and `ThirdPartyNotices.txt` (the libraries
ONNX Runtime itself includes) in its `licenses` folder.

## Depth models

The model files are not in this repository. A package may ship some of them in its `models`
folder; each is a static-shape ONNX conversion made for this app of the authors' released
weights, under the authors' licence.

| Model | File family | Licence | Source |
|---|---|---|---|
| Distill-Any-Depth Base | `dad-base_*.onnx` | MIT | [Westlake-AGI-Lab/Distill-Any-Depth](https://github.com/Westlake-AGI-Lab/Distill-Any-Depth) |
| Depth Anything V2 Small | `da2-small_*.onnx` | Apache-2.0 | [DepthAnything/Depth-Anything-V2](https://github.com/DepthAnything/Depth-Anything-V2) (Small only; Base and Large are CC-BY-NC-4.0 and not used) |
| Depth Anything 3 Small and Base | `da3-small_*.onnx`, `da3-base_*.onnx` | Apache-2.0 | [ByteDance-Seed/Depth-Anything-3](https://github.com/ByteDance-Seed/Depth-Anything-3), ONNX exports from `onnx-community` |
| Video Depth Anything Small | `vda-small_*.onnx`, `vda-small-first_*.onnx` | Apache-2.0 | [DepthAnything/Video-Depth-Anything](https://github.com/DepthAnything/Video-Depth-Anything) (Small only; Large is CC-BY-NC-4.0 and not used) |

Check each source's current licence before shipping its files: model licences differ by size,
and the non-commercial ones (Depth Anything V2 Base and Large, Video Depth Anything Large,
Depth Anything 3 Large and Giant) must never be shipped.

## Used, not shipped

- **TensorRT for RTX execution provider** (NVIDIA): installed by Windows ML as a Windows package
  on PCs with an NVIDIA RTX GPU; the app finds and loads it, under NVIDIA's terms.
- **Direct3D shader compiler** (`d3dcompiler_47.dll`), part of Windows.

## The MIT licence

The MIT-licensed components above are provided under these terms, with their copyright lines
as listed:

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software
> and associated documentation files (the "Software"), to deal in the Software without
> restriction, including without limitation the rights to use, copy, modify, merge, publish,
> distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
> Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or
> substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING
> BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
> NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
> DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

The Apache License 2.0 is at <https://www.apache.org/licenses/LICENSE-2.0>.
