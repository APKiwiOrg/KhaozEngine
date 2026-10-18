# GPU shard build cost measurement

Issue https://github.com/APKiwiOrg/KhaozEngine/issues/1472

Chosen design A. Corrected A completed in 16m42s, 3m06s faster than the 19m48s baseline and 7m09s faster than B's 23m51s. Its shard compilation fell from 3,754 to 1,864 runner-seconds, a 50.3% reduction. Scoped builds also reach ready binaries sooner than B on every shard type. Keep A's independent builds and discard B's artifact transfer path.

A's Metal and Vulkan full-suite checks are green. Windows is red because of the existing ReportRedactor failure tracked by #1475, described below. B passed all three summaries. The first A dispatch is disqualified because omitted project references built in Debug.

All trials use the standard hosted runners and the same engine source at `778c184bd`. No engine version or changelog changes. No test retries or load tests. Three default dispatches total, including the cancelled invalid A trial.

| Trial | Run wall | Longest job | Shard compilation | Full suite green | Result |
|---|---:|---|---:|---:|---|
| [baseline](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982) | 19m48s | [windows-latest / direct3d11-native / render-3](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585535) 18m53s | 3754 s | 3/3 | success |
| [A-invalid](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398) | 19m14s | [windows-latest / direct3d11-native / rest](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444172) 18m41s | 1932 s | 0/3 | cancelled |
| [B](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494) | 23m51s | [windows-latest / direct3d11-native / render-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969404) 23m23s | 486 s | 3/3 | success |
| [A-fixed](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516) | 16m42s | [ubuntu-latest / vulkan-native / render-5](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206556) 16m18s | 1864 s | 2/3 | failure |

Shard compilation sums the matrix build steps and excludes the unchanged sync-validation job. Run wall is workflow creation to the last job completion. Queue time is job creation to start, using the jobs API. All durations below use step or job timestamps. Empty step cells mean that step did not run.

## Build shapes

A builds only each shard's test roots and their references. Windows rest also builds Render.Tests for FXC validation, and Vulkan rest builds it for shader-cache verification. Unsharded triggers retain their original full-solution restore and build.

B uses each existing rest shard as its OS's build producer. It builds the original solution once, archives the Release bin directories, uploads them before its tests and lets sibling shards restore packages and download the output. This preserves the three-job Metal cap without an extra producer job. The measured wait includes the producer dependency and up to five seconds of polling.

Both preserve the Windows-only NuGet cache and exclude KhaozEngine extractions. Both preserve test selection, check-render, the per-backend exactly-once summaries, primary rest instrumentation and the unsharded paths.

## Release configuration regression

The first A run built dependencies omitted from its reduced solution in Debug. Its Metal rest log contains Debug output paths and 64 failed test rows, including allocation assertions and Debug.Fail. It was cancelled after the configuration regression was established. Those timings are not valid savings.

The corrected builder sets `ShouldUnsetParentConfigurationAndPlatform=false` and rejects Debug bin directories before tests. Microsoft documents the [configuration removal behavior](https://learn.microsoft.com/dotnet/api/microsoft.build.tasks.assignprojectconfiguration.shouldunsetparentconfigurationandplatform). A real three-project SDK fixture, with only its root in the solution, fails compilation with the original command and passes with the corrected builder. The actual Render.Tests graph also builds locally in Release with zero warnings and errors.

## Complete jobs API measurements

### baseline

Run https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982
Commit 778c184bd5b7d7fa155a09de5850de9b053005ce
Status completed, result success, elapsed wall 19:48

| Job | Queue | Duration | Restore | Build | Archive | Upload | Wait | Download | Extract | Result |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| [macos-26 / metal-native / full suite](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117542092759) | 0:05 | 0:11 |  |  |  |  |  |  |  | success |
| [macos-26 / metal-native / render-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585570) | 0:09 | 15:29 | 0:27 | 4:04 |  |  |  |  |  | success |
| [macos-26 / metal-native / render-2](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585485) | 0:06 | 9:03 | 0:13 | 1:55 |  |  |  |  |  | success |
| [macos-26 / metal-native / rest](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585510) | 0:08 | 12:13 | 0:35 | 3:10 |  |  |  |  |  | success |
| [select legs](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538554755) | 0:03 | 0:06 |  |  |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native (sync validation)](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585242) | 0:02 | 6:18 | 0:16 | 2:09 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / full suite](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117542092653) | 0:03 | 0:37 |  |  |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / projects-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585518) | 0:03 | 10:44 | 0:19 | 2:41 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585588) | 0:03 | 14:55 | 0:19 | 3:13 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-10](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585685) | 0:03 | 15:27 | 0:19 | 2:32 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-11](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585637) | 0:03 | 14:25 | 0:19 | 3:00 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-12](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585727) | 0:03 | 12:45 | 0:17 | 2:03 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-2](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585575) | 0:03 | 12:28 | 0:16 | 2:08 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-3](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585551) | 0:03 | 11:47 | 0:17 | 2:14 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-4](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585585) | 0:02 | 15:22 | 0:19 | 2:54 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-5](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585539) | 0:03 | 18:08 | 0:19 | 3:09 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-6](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585530) | 0:03 | 18:32 | 0:18 | 2:53 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-7](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585543) | 0:03 | 17:16 | 0:20 | 3:01 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-8](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585599) | 0:03 | 13:59 | 0:19 | 2:42 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-9](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585635) | 0:03 | 15:02 | 0:18 | 3:01 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / rest](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585503) | 0:03 | 12:12 | 0:19 | 2:54 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / full suite](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117542092645) | 0:03 | 0:09 |  |  |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / projects-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585509) | 0:03 | 13:45 | 0:16 | 2:35 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / render-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585600) | 0:03 | 12:21 | 0:09 | 1:36 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / render-2](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585586) | 0:03 | 13:46 | 0:16 | 2:19 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / render-3](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585535) | 0:03 | 18:53 | 0:16 | 2:43 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / render-4](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585516) | 0:03 | 17:52 | 0:16 | 2:51 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / rest](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585550) | 0:03 | 17:02 | 0:17 | 2:56 |  |  |  |  |  | success |

Longest job [windows-latest / direct3d11-native / render-3](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39081283982/job/117538585535) 18:53

| Backend | Shard type | Restore range | Build range | Median build |
|---|---|---:|---:|---:|
| direct3d11-native | projects | 16 to 16 s | 155 to 155 s | 155 s |
| direct3d11-native | render | 9 to 16 s | 96 to 171 s | 151 s |
| direct3d11-native | rest | 17 to 17 s | 176 to 176 s | 176 s |
| metal-native | render | 13 to 27 s | 115 to 244 s | 179.5 s |
| metal-native | rest | 35 to 35 s | 190 to 190 s | 190 s |
| vulkan-native | projects | 19 to 19 s | 161 to 161 s | 161 s |
| vulkan-native | render | 16 to 20 s | 123 to 193 s | 173.5 s |
| vulkan-native | rest | 19 to 19 s | 174 to 174 s | 174 s |

### A-invalid

Run https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398
Commit f231c01a0ba9e89dae1f3e792db3001ff3c438c5
Status completed, result cancelled, elapsed wall 19:14

| Job | Queue | Duration | Restore | Build | Archive | Upload | Wait | Download | Extract | Result |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| [macos-26 / metal-native / full suite](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117552960598) | 0:03 | 0:09 |  |  |  |  |  |  |  | failure |
| [macos-26 / metal-native / render-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444212) | 0:11 | 11:29 | 0:11 | 1:41 |  |  |  |  |  | success |
| [macos-26 / metal-native / render-2](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444227) | 0:09 | 11:26 | 0:09 | 1:33 |  |  |  |  |  | success |
| [macos-26 / metal-native / rest](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444192) | 0:06 | 11:33 | 0:12 | 1:30 |  |  |  |  |  | failure |
| [select legs](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549414007) | 0:04 | 0:06 |  |  |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native (sync validation)](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549443978) | 0:04 | 7:46 | 0:17 | 2:40 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / full suite](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117552960639) | 0:05 | 0:13 |  |  |  |  |  |  |  | failure |
| [ubuntu-latest / vulkan-native / projects-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444199) | 0:03 | 18:39 | 0:06 | 1:05 |  |  |  |  |  | cancelled |
| [ubuntu-latest / vulkan-native / render-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444171) | 0:03 | 12:20 | 0:07 | 1:19 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-10](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444205) | 0:03 | 9:51 | 0:06 | 0:53 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-11](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444159) | 0:03 | 10:16 | 0:07 | 1:06 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-12](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444207) | 0:02 | 17:42 | 0:13 | 1:29 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-2](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444216) | 0:03 | 14:49 | 0:08 | 1:30 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-3](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444268) | 0:03 | 10:49 | 0:07 | 1:08 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-4](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444222) | 0:03 | 9:17 | 0:06 | 0:57 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-5](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444182) | 0:03 | 13:10 | 0:08 | 1:06 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-6](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444161) | 0:03 | 16:48 | 0:08 | 1:24 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-7](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444204) | 0:03 | 10:34 | 0:06 | 0:56 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-8](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444165) | 0:03 | 13:10 | 0:09 | 1:29 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-9](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444228) | 0:03 | 12:54 | 0:08 | 1:27 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / rest](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444166) | 0:03 | 15:51 | 0:16 | 2:29 |  |  |  |  |  | failure |
| [windows-latest / direct3d11-native / full suite](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117552960595) | 0:03 | 0:11 |  |  |  |  |  |  |  | failure |
| [windows-latest / direct3d11-native / projects-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444151) | 0:03 | 8:49 | 0:04 | 0:24 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / render-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444152) | 0:03 | 18:30 | 0:08 | 1:32 |  |  |  |  |  | cancelled |
| [windows-latest / direct3d11-native / render-2](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444147) | 0:03 | 15:04 | 0:09 | 1:35 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / render-3](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444145) | 0:03 | 17:41 | 0:07 | 1:32 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / render-4](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444178) | 0:04 | 17:36 | 0:07 | 1:31 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / rest](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444172) | 0:04 | 18:41 | 0:14 | 2:36 |  |  |  |  |  | cancelled |

Longest job [windows-latest / direct3d11-native / rest](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39084941398/job/117549444172) 18:41

| Backend | Shard type | Restore range | Build range | Median build |
|---|---|---:|---:|---:|
| direct3d11-native | projects | 4 to 4 s | 24 to 24 s | 24 s |
| direct3d11-native | render | 7 to 9 s | 91 to 95 s | 92 s |
| direct3d11-native | rest | 14 to 14 s | 156 to 156 s | 156 s |
| metal-native | render | 9 to 11 s | 93 to 101 s | 97 s |
| metal-native | rest | 12 to 12 s | 90 to 90 s | 90 s |
| vulkan-native | projects | 6 to 6 s | 65 to 65 s | 65 s |
| vulkan-native | render | 6 to 13 s | 53 to 90 s | 73.5 s |
| vulkan-native | rest | 16 to 16 s | 149 to 149 s | 149 s |

### B

Run https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494
Commit f49f4398eb74148f0df80f1d2bc887a543138bb6
Status completed, result success, elapsed wall 23:51

| Job | Queue | Duration | Restore | Build | Archive | Upload | Wait | Download | Extract | Result |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| [macos-26 / metal-native / full suite](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117560052497) | 0:02 | 0:12 |  |  |  |  |  |  |  | success |
| [macos-26 / metal-native / render-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969485) | 0:09 | 15:38 | 0:16 |  |  |  | 3:53 | 1:37 | 0:13 | success |
| [macos-26 / metal-native / render-2](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969418) | 0:08 | 10:41 | 0:18 |  |  |  | 3:54 | 0:14 | 0:11 | success |
| [macos-26 / metal-native / rest](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969450) | 0:07 | 14:44 | 0:23 | 2:31 | 0:46 | 0:16 |  |  |  | success |
| [select legs](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555943759) | 0:03 | 0:05 |  |  |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native (sync validation)](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969255) | 0:04 | 8:21 | 0:18 | 2:56 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / full suite](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117560052481) | 0:02 | 0:10 |  |  |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / projects-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969468) | 0:04 | 13:13 | 0:19 |  |  |  | 3:50 | 0:13 | 0:25 | success |
| [ubuntu-latest / vulkan-native / render-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969405) | 0:03 | 15:44 | 0:18 |  |  |  | 3:55 | 0:11 | 0:25 | success |
| [ubuntu-latest / vulkan-native / render-10](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969519) | 0:04 | 18:44 | 0:18 |  |  |  | 3:54 | 0:27 | 0:24 | success |
| [ubuntu-latest / vulkan-native / render-11](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969648) | 0:04 | 11:40 | 0:14 |  |  |  | 3:57 | 0:26 | 0:17 | success |
| [ubuntu-latest / vulkan-native / render-12](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969500) | 0:03 | 18:38 | 0:19 |  |  |  | 3:48 | 1:03 | 0:25 | success |
| [ubuntu-latest / vulkan-native / render-2](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969464) | 0:04 | 17:35 | 0:18 |  |  |  | 3:51 | 0:21 | 0:25 | success |
| [ubuntu-latest / vulkan-native / render-3](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969459) | 0:03 | 16:30 | 0:18 |  |  |  | 3:47 | 0:49 | 0:24 | success |
| [ubuntu-latest / vulkan-native / render-4](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969400) | 0:03 | 12:27 | 0:13 |  |  |  | 3:57 | 0:26 | 0:18 | success |
| [ubuntu-latest / vulkan-native / render-5](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969525) | 0:04 | 19:22 | 0:18 |  |  |  | 3:53 | 0:31 | 0:24 | success |
| [ubuntu-latest / vulkan-native / render-6](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969541) | 0:04 | 18:05 | 0:18 |  |  |  | 3:47 | 0:27 | 0:24 | success |
| [ubuntu-latest / vulkan-native / render-7](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969544) | 0:04 | 16:15 | 0:18 |  |  |  | 3:50 | 0:39 | 0:21 | success |
| [ubuntu-latest / vulkan-native / render-8](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969555) | 0:04 | 16:34 | 0:20 |  |  |  | 3:51 | 0:47 | 0:24 | success |
| [ubuntu-latest / vulkan-native / render-9](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969511) | 0:04 | 16:00 | 0:19 |  |  |  | 3:54 | 0:13 | 0:24 | success |
| [ubuntu-latest / vulkan-native / rest](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969461) | 0:04 | 12:39 | 0:18 | 2:52 | 0:46 | 0:12 |  |  |  | success |
| [windows-latest / direct3d11-native / full suite](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117560052487) | 0:02 | 0:10 |  |  |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / projects-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969378) | 0:03 | 15:19 | 0:19 |  |  |  | 3:59 | 0:34 | 0:24 | success |
| [windows-latest / direct3d11-native / render-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969404) | 0:03 | 23:23 | 0:21 |  |  |  | 2:59 | 0:46 | 0:32 | success |
| [windows-latest / direct3d11-native / render-2](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969451) | 0:04 | 19:35 | 0:17 |  |  |  | 4:12 | 1:01 | 0:26 | success |
| [windows-latest / direct3d11-native / render-3](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969413) | 0:03 | 20:48 | 0:16 |  |  |  | 4:20 | 0:12 | 0:26 | success |
| [windows-latest / direct3d11-native / render-4](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969477) | 0:04 | 20:03 | 0:17 |  |  |  | 4:21 | 0:20 | 0:24 | success |
| [windows-latest / direct3d11-native / rest](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969483) | 0:04 | 18:56 | 0:16 | 2:43 | 1:11 | 0:15 |  |  |  | success |

Longest job [windows-latest / direct3d11-native / render-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39087131494/job/117555969404) 23:23

| Backend | Shard type | Restore range | Build range | Median build |
|---|---|---:|---:|---:|
| direct3d11-native | projects | 19 to 19 s |  |  |
| direct3d11-native | render | 16 to 21 s |  |  |
| direct3d11-native | rest | 16 to 16 s | 163 to 163 s | 163 s |
| metal-native | render | 16 to 18 s |  |  |
| metal-native | rest | 23 to 23 s | 151 to 151 s | 151 s |
| vulkan-native | projects | 19 to 19 s |  |  |
| vulkan-native | render | 13 to 20 s |  |  |
| vulkan-native | rest | 18 to 18 s | 172 to 172 s | 172 s |

### A-fixed

Run https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516
Commit cb207551d37f7e211e394ea9562fe5e347252c8b
Status completed, result failure, elapsed wall 16:42

| Job | Queue | Duration | Restore | Build | Archive | Upload | Wait | Download | Extract | Result |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| [macos-26 / metal-native / full suite](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117563213413) | 0:04 | 0:10 |  |  |  |  |  |  |  | success |
| [macos-26 / metal-native / render-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206549) | 0:07 | 11:21 | 0:09 | 0:46 |  |  |  |  |  | success |
| [macos-26 / metal-native / render-2](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206499) | 0:11 | 9:51 | 0:14 | 1:23 |  |  |  |  |  | success |
| [macos-26 / metal-native / rest](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206536) | 0:06 | 9:22 | 0:17 | 1:48 |  |  |  |  |  | success |
| [select legs](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560187148) | 0:03 | 0:03 |  |  |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native (sync validation)](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206444) | 0:04 | 7:58 | 0:18 | 2:45 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / full suite](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117563213508) | 0:04 | 0:09 |  |  |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / projects-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206562) | 0:03 | 10:12 | 0:09 | 1:22 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206577) | 0:03 | 12:45 | 0:07 | 1:29 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-10](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206555) | 0:04 | 11:50 | 0:06 | 1:05 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-11](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206519) | 0:03 | 12:28 | 0:07 | 1:26 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-12](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206565) | 0:03 | 12:11 | 0:07 | 1:09 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-2](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206590) | 0:04 | 14:19 | 0:09 | 1:29 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-3](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206611) | 0:04 | 13:09 | 0:09 | 1:29 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-4](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206587) | 0:03 | 13:22 | 0:09 | 1:29 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-5](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206556) | 0:03 | 16:18 | 0:08 | 1:27 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-6](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206517) | 0:03 | 13:41 | 0:08 | 1:03 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-7](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206573) | 0:03 | 15:12 | 0:09 | 1:28 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-8](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206510) | 0:03 | 12:43 | 0:09 | 1:28 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / render-9](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206578) | 0:03 | 11:10 | 0:06 | 1:10 |  |  |  |  |  | success |
| [ubuntu-latest / vulkan-native / rest](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206539) | 0:04 | 11:38 | 0:17 | 2:53 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / full suite](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117563213471) | 0:03 | 0:11 |  |  |  |  |  |  |  | failure |
| [windows-latest / direct3d11-native / projects-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206595) | 0:04 | 13:57 | 0:05 | 0:16 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / render-1](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206575) | 0:04 | 12:10 | 0:05 | 0:55 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / render-2](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206598) | 0:04 | 12:07 | 0:06 | 1:07 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / render-3](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206514) | 0:03 | 14:17 | 0:06 | 1:05 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / render-4](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206621) | 0:03 | 16:12 | 0:08 | 1:31 |  |  |  |  |  | success |
| [windows-latest / direct3d11-native / rest](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206601) | 0:03 | 12:03 | 0:08 | 1:46 |  |  |  |  |  | failure |

Longest job [ubuntu-latest / vulkan-native / render-5](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206556) 16:18

| Backend | Shard type | Restore range | Build range | Median build |
|---|---|---:|---:|---:|
| direct3d11-native | projects | 5 to 5 s | 16 to 16 s | 16 s |
| direct3d11-native | render | 5 to 8 s | 55 to 91 s | 66 s |
| direct3d11-native | rest | 8 to 8 s | 106 to 106 s | 106 s |
| metal-native | render | 9 to 14 s | 46 to 83 s | 64.5 s |
| metal-native | rest | 17 to 17 s | 108 to 108 s | 108 s |
| vulkan-native | projects | 9 to 9 s | 82 to 82 s | 82 s |
| vulkan-native | render | 6 to 9 s | 63 to 89 s | 87.5 s |
| vulkan-native | rest | 17 to 17 s | 173 to 173 s | 173 s |

## Verification

| Command | Exit | Evidence |
|---|---:|---|
| `build-slot --label gpu-shard-final-1472 -- sh scripts/tests/gpu-test-shards.test.sh` | 0 | 64 passed, including the actual MSBuild fixture |
| `build-slot --label gpu-shard-release-red-1472 -- sh scripts/tests/gpu-shard-build.test.sh artifacts/ci-1472/build-before.sh` | 1 | Expected CS1029 with the original build command |
| `build-slot --label gpu-shard-release-green-1472 -- sh scripts/tests/gpu-shard-build.test.sh` | 0 | Root, middle and leaf outputs all Release |
| `build-slot --label gpu-shard-render-release-1472 -- sh -c 'dotnet restore artifacts/ci-1472/local-render.slnx && sh scripts/gpu-shard-build.sh artifacts/ci-1472/local-render.slnx'` | 0 | 55 project output rows, all Release, zero warnings and errors |
| Release Render.Tests listing and `check-render` for all three backends through build-slot | 0 | 8,521 methods, zero unassigned or doubled on every backend |
| `shellcheck scripts/gpu-shard-build.sh scripts/tests/gpu-shard-build.test.sh` | 0 | No findings |
| `actionlint .github/workflows/cross-platform-gpu.yml` | 1 | Three unchanged main findings, two ubuntu-26.04 label notices and SC2012 in the existing native-file listing |
| `sh scripts/check-dashes.sh --tree` | 0 | Whole tree |
| `sh scripts/check-prose.sh --tree` | 0 | Whole tree |
| `sh scripts/check-file-size.sh --tree` | 0 | Whole tree |
| `sh scripts/check-agent-instructions.sh --tree` | 0 | Whole tree |
| `bash scripts/check-doc-versions.sh` | 0 | Engine version and documentation declarations unchanged |
| `gh run watch 39084941398 --exit-status` | 1 | Cancelled invalid A trial |
| `gh run watch 39087131494 --exit-status` | 0 | B, all shards and summaries green |
| `gh run watch 39088564516 --exit-status` | 1 | Corrected A, known #1475 on Windows rest |

No manual validation is needed. Integration belongs to the owning orchestrator. No merge, main push or tag was performed.

## Time from restore completion to ready binaries

This isolates the changed phase from test runtime. For B, ready means upload completion on rest and extraction completion on a recipient. Waiting for the producer is included. Values are medians in seconds, with ranges in parentheses.

| Backend | Shard type | Baseline | B | Corrected A |
|---|---|---:|---:|---:|
| direct3d11-native | projects | 155 (155 to 155) | 297 (297 to 297) | 16 (16 to 16) |
| direct3d11-native | render | 151 (96 to 171) | 301.5 (257 to 339) | 66 (55 to 91) |
| direct3d11-native | rest | 176 (176 to 176) | 249 (249 to 249) | 106 (106 to 106) |
| metal-native | render | 179.5 (115 to 244) | 301 (259 to 343) | 64.5 (46 to 83) |
| metal-native | rest | 190 (190 to 190) | 213 (213 to 213) | 108 (108 to 108) |
| vulkan-native | projects | 161 (161 to 161) | 268 (268 to 268) | 82 (82 to 82) |
| vulkan-native | render | 173.5 (123 to 193) | 283 (271 to 317) | 87.5 (63 to 89) |
| vulkan-native | rest | 174 (174 to 174) | 230 (230 to 230) | 173 (173 to 173) |

## Shared output artifact sizes

| Artifact | Bytes |
|---|---:|
| release-output-metal-native | 1303139486 |
| release-output-direct3d11-native | 1308103344 |
| release-output-vulkan-native | 1323280036 |

## Scheduling limitation

The required run-list check was clear immediately before each dispatch. Another equipment-coverage run was queued immediately after B started. Every job's queue time is retained above. Each valid option has one default dispatch, so differences in hosted test runtime are observations rather than a statistical speed guarantee.

## Discovered work and unrelated failure

The [corrected A Windows rest job](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117560206601) failed only `ReportRedactorTests.Catastrophic_input_is_redacted_in_linear_time`. The repeated-bearer shape at line 260 returned `[redacted]` instead of the expected text. This is the same wall-clock backstop failure family tracked by [#1475](https://github.com/APKiwiOrg/KhaozEngine/issues/1475), which originally recorded the repeated query-token shape at line 251. No retry and no test change.

The [Windows full-suite summary](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/39088564516/job/117563213471) correctly failed the red rest shard. Its assembly rows still show each non-Render assembly on exactly its assigned shard. The summary has no missing or duplicate assembly error.

The Debug-reference fallback discovered in the first A trial is fixed in this issue and protected by the SDK regression fixture and the CI output guard. No separate out-of-scope work was actioned.
