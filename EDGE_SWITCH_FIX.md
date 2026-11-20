# 边缘切换修复（3.8.1）

基于 `9525ee8`（3.8.0），修复可以自动复现的边缘切换失效路径：

- Windows 鼠标控制 Ubuntu 时，旧代码要求先走到共享边内侧 100px 才能返回。在边缘附近完成操作后，无论向外推多少次都不会返回。现在防抖最多持续 300ms，向内走够距离仍可提前解除。
- Ubuntu 鼠标控制 Windows 时，旧代码要求先向内走 80px，且每次超过 60px 的外推都会被丢弃。现在保留 400ms 入场防抖，之后按共享边位置与外推方向交还控制，不要求移动速度或累计距离。
- Ubuntu 将超过 80px 的真实移动误认为程序移位，既丢失输入又跳过边缘回中。现在记录程序实际的回中目标，等待对应 X 事件并忽略其前面的旧事件，不再按移动大小猜测。
- XSync / XQueryPointer 可以把事件读入 Xlib 缓冲区，此时 GLib 的文件描述符监听不会收到新通知。现在现有 60ms 定时回调也处理缓存事件，避免热键、回中事件及鼠标输入等待其他 X 流量。

修复前，四方向近边返回测试全部失败；反向近边返回与快速外推共 8 个场景全部失败。只临时解除返回状态即可让第一组通过，验证了永久防抖条件的因果关系。Ubuntu 的快速移动、短距离程序移位、已缓存热键测试也分别在修复前失败。

验证结果：Windows 原有断言通过；新增四方向快推、近边返回、入场防抖、停留、内移、开关检查通过；真实鼠标回调重放 100 次往返通过；Ubuntu 私有 Xvfb 上 23 个测试全部通过，包含真实 X11 → 控制连接的快速移动与回中、热键、断线释放、剪贴板和文件传输。

复查命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Output dist/TypeBridge.exe -Test
powershell -NoProfile -ExecutionPolicy Bypass -File tests/Run-DesktopChecks.ps1 -Executable dist/TypeBridge.Tests.exe -Argument --edge-runtime
```

```bash
python3 tests/run_linux.py
bash ubuntu/build-deb.sh
```

测试在独立桌面和独立 X server 上执行。未用自动化鼠标操作当前用户桌面；实际硬件上的长期体验仍需观察。排查时 Ubuntu 主机负载曾达到约 218，产生额外超时；这类主机调度延迟不属于本补丁可以消除的范围。
