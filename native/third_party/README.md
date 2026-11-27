# Native third-party dependencies

CMake uses these files directly from the checkout. The Vulkan headers are the C headers
required by the headless build, including the video headers that Vulkan core includes.
The full file list and hashes are below.

| Component | Version | Upstream repository | Release tag | Commit | Archive SHA-256 | SPDX license | Files kept | Local patches |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| doctest | 2.5.0 | https://github.com/doctest/doctest | v2.5.0 | d44d4f6e66232d716af82f00a063759e9d0e50d6 | eb917c80bef7aceb9eca59d9328142351facdcdabe90b5242632b93c34b9e345 | MIT | `doctest/doctest.h`, `LICENSE.txt` | None |
| volk | 1.4.363 | https://github.com/zeux/volk | vulkan-sdk-1.4.363.0 | 54fc0d7a72887cbfc5b030d26897b2b40ac53fb8 | 1547d8d74395d4048fb3f4a6313da56db626e89b55db238d0d7e8944c8a645f3 | MIT | `volk.c`, `volk.h`, `LICENSE.md` | None |
| vulkan-headers | 1.4.363 | https://github.com/KhronosGroup/Vulkan-Headers | vulkan-sdk-1.4.363.0 | 6802bb4733b63ed5efd3adb308a6c885ef180ea1 | 4a078be12bef21cfebc09d878b77a63cff9d68f899254a0b00d0e37ef73e7f7e | Apache-2.0 OR MIT | `include/vulkan/{vk_platform,vulkan,vulkan_core}.h`, `include/vk_video/*.h`, `LICENSE.md`, `LICENSES/{Apache-2.0,MIT}.txt` | None |
| vma | 3.5.0 | https://github.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator | v3.5.0 | 0f1a757da579831283413295cc7f3b47eb521c77 | c5f3db848fca9cb9726b41f256cc3007773321fa92a86d1e0902e66474e8a9e3 | MIT | `include/vk_mem_alloc.h`, `LICENSE.txt` | None |

From `native/`, run `scripts/refresh-third-party.sh --check` to verify the vendored bytes offline.
To restore a component, run `scripts/refresh-third-party.sh <component>`. It downloads the pinned
GitHub tag archive and verifies both the archive and its kept files before copying them into place.
Keep the two Vulkan SDK tags matched when updating these pins.

## File SHA-256

```text
0fe0b331fa1513dcce8604ff1fa925f32d1cea17d8aeb1c2471fad40d291adc5  doctest/LICENSE.txt
a58efc9446d70ddd5dd3b7724ebb8742882860f36f46da64d62993b02911fb6f  doctest/doctest/doctest.h
52df2c03d6cfc9ffec13c9d3626c530fc9ce0cbe41d5ea3d10cd46edeb1aeb38  vma/LICENSE.txt
41a5897caee36a46d636873a1f6a2518371d4d419cd299d40457e63bde64ecea  vma/include/vk_mem_alloc.h
04a0693a84f19e53d281ca98bbb0c86ca77251ab13769c6168e6684feb9a1436  volk/LICENSE.md
64159146b843e1b3d9f5dc14035b2716e16868b9e5ec4d7aef8f57f6b674a875  volk/volk.c
458079eb76ba20ec5cb97bb696e82e94c6662b67dbbc5150cb13e691aeab7e06  volk/volk.h
95ad366d23fadf701d355bc45fb8b82ae2d700239471d35d41286ac3b08ff903  vulkan-headers/LICENSE.md
cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30  vulkan-headers/LICENSES/Apache-2.0.txt
1ca3502222d967f3be5751c55f6b7ee735b5383909c3b501495f54b216dbf227  vulkan-headers/LICENSES/MIT.txt
c75c1d324b97d247aef0008024bc7f3adb98c741ab7f66c882ec38fdacc7ee33  vulkan-headers/include/vk_video/vulkan_video_codec_av1std.h
7e3a1ce177c12546d410f3179ce1b81f2da7a8eba4f525b29cd563ab4099c0e5  vulkan-headers/include/vk_video/vulkan_video_codec_av1std_decode.h
8d166b4543260a38347860443b1a59c7a5e86cdb0d0facaf4c704f667de030e3  vulkan-headers/include/vk_video/vulkan_video_codec_av1std_encode.h
fded484cef9f90fbbd089e0647268f36e5f4292179fbc5428a2c9e8d7709bbe9  vulkan-headers/include/vk_video/vulkan_video_codec_h264std.h
37b970c3d80536ad1ac074cfe19b58ee03c3075378e02179e1dc5e4351266821  vulkan-headers/include/vk_video/vulkan_video_codec_h264std_decode.h
227e092b53c4e7ca1a948ed021704511c1ed16040cd6188ff6703e5ae66db64d  vulkan-headers/include/vk_video/vulkan_video_codec_h264std_encode.h
0b81f8986ada015ef2e127449eff9d9634899bb59a0a9277a4054e9ec4416c12  vulkan-headers/include/vk_video/vulkan_video_codec_h265std.h
879a0dd370a1b1ad184638906c52bdfe7d80d639fbc5a5baa8b02d7c5b60b147  vulkan-headers/include/vk_video/vulkan_video_codec_h265std_decode.h
abb3e72af22e4e0a3dbe5dff7be1b275949388809fb3987830341e8f495ea1c7  vulkan-headers/include/vk_video/vulkan_video_codec_h265std_encode.h
0a47125865376a3fe7014b69ff6db9d04e30ebf8c4d15664f1d894649ad5c09d  vulkan-headers/include/vk_video/vulkan_video_codec_vp9std.h
1ceb1a8d0e3370e508cf688a6e57dc314cd82186b60f3cef420ea4b1b483865d  vulkan-headers/include/vk_video/vulkan_video_codec_vp9std_decode.h
d2e7caa396c521d03d491a269572b1d31b925b5127d22fda14199951ebae89f8  vulkan-headers/include/vk_video/vulkan_video_codecs_common.h
a2cd9085c66776845d2524c3b0e73ccf4827ba046aa6f676c4b8ddbed47d6552  vulkan-headers/include/vulkan/vk_platform.h
096f50152d0b298d8df84a561a37d8190c8a60af34dff0e2cc8328b24491a640  vulkan-headers/include/vulkan/vulkan.h
2f64fd6c7c3f342b0e44f039f1c359b7f0e6cfcb71a7dca1cc5156282faa3b51  vulkan-headers/include/vulkan/vulkan_core.h
```
