# Component licenses

- **RouteMeter macOS and Windows viewers**: original project code under the MIT license in LICENSE.
- **RouteMeter Core**: a modified build of MetaCubeX/Mihomo v1.19.33, commit `3f27ef39f8c96bbfbc8b0b9bb10f5c9ef60149d8`, under GPL-3.0. The original license is in engine/licenses/GPL-3.0.txt and accompanies binary packages.
- **engine/meter and engine/patches**: RouteMeter additions to that core, GPL-3.0-only as marked in their source headers. The patch also modifies upstream GPL-3.0 source. Modified core builds identify themselves as RouteMeterCore.
- The matching release provides a complete patched corresponding-source archive, including the upstream license, module manifests/checksums and build instructions. Upstream and dependency notices remain in that source tree.
- Windows self-contained viewer packages include the Microsoft .NET runtime. Its applicable Microsoft notices and redistribution terms accompany the runtime/build tooling. No .NET code is linked into the separately built routing core.

Viewers and core are separate programs communicating over a documented, authenticated read-only loopback statistics API. Nothing here changes the license of Mihomo or any dependency. Quantumult X is not distributed with RouteMeter.
