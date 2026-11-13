# 重复文件清理小工具

飞牛原生应用，按当前 NAS 用户授权目录查找完整重复文件，支持 Btrfs 区块合并、应用回收站／永久删除、硬链接替换。任务和结果保存在应用数据目录，可关闭页面后台执行。

## 使用

1. 从飞牛桌面打开应用，添加授权目录，选择一个或多个目录开始查重。
2. 查看重复组及共享状态，调整保留文件，选择清理方式并检查预览。
3. 确认后开始后台清理；在任务页查看进度、成功／失败和空间统计。

Btrfs 合并保留独立文件与写时复制语义；硬链接共用内容和元数据，两者均不能跨不兼容的文件系统。应用回收站与飞牛系统回收站独立，移入后尚未释放空间，支持恢复且不覆盖同名文件。永久删除不可恢复。清理期间应保持所选文件静止；应用检测到内容变化会停止相关操作，不能替其他程序提供全局文件锁。

## 空间统计

- 文件系统：按目录及实际文件句柄识别，不能用应用安装盘代替用户文件所在盘。
- 已有硬链接：通过文件系统内的 inode 身份判断，不重复计入空间收益。
- 共享区块：FIEMAP 的共享标志仅表示存在其他引用。还需在同一文件系统比较对应偏移的区块映射，才能确认与保留文件共享；快照不必属于当前重复组。
- 估算释放量：仅计入成功完成且可确认独占的普通数据区块，不包含元数据变化；压缩、内联、延迟分配、额外引用等不能确定的部分标为统计不完整。不是文件逻辑大小，也不是 ioctl 的处理字节数。完全无法估算时显示“无法确定”。
- 存储池变化：记录开始、结束和两秒后的可用空间，允许负值。其他应用写入、快照和延迟回收也会影响数值，不能把全部变化归因于本任务。

## 本地构建与测试

需要 `global.json` 指定的 .NET 10 SDK、Node.js 22。依赖版本保存在锁文件中。

```sh
npm --prefix frontend ci
npm --prefix frontend run build
npm --prefix frontend test
dotnet run --project tests/Tests.csproj
```

非 Linux 本地只运行空间分析、SQLite、权限边界等通用测试。完整原生测试必须在 **非 root Linux 用户**下运行；`scripts/linux-integration.sh` 在一次性 CI 环境准备 ext4/Btrfs loop 镜像，然后以 nobody 执行真实 AOT 测试。准备测试磁盘的 root 步骤不属于应用安装或运行流程。

本地前端开发：`npm --prefix frontend run dev`，开发服务器只监听本机，实际授权及后台操作仍需 fnOS。不会提供绕过网关或授权的开发 API。

## Linux AOT / FPK

发布需要 Linux 与对应 CPU 架构的编译环境及 clang、zlib 开发包；不能直接用 macOS 编译 Linux Native AOT。CI 在两种原生架构的 Ubuntu 22.04 容器中构建，避免新 runner 带来更高 glibc 要求。NAS 运行不需要 Docker 或 .NET 安装。

```sh
dotnet publish backend/DuplicateCleanup.csproj -c Release -r linux-x64 -o .build/amd64
node scripts/build_fpk.mjs amd64 .build/amd64
# 在 ARM64 Linux 构建环境中：
dotnet publish backend/DuplicateCleanup.csproj -c Release -r linux-arm64 -o .build/arm64
node scripts/build_fpk.mjs arm64 .build/arm64
```

脚本沿用端口转发应用的 FPK 外层／app.tgz 结构，并检查 AOT 主程序和 SQLite 原生库的 ELF 架构。生成两个独立 FPK，manifest 分别声明 x86/arm，附 SHA-256。推送分支构建 artifacts，推送 `v0.1.0` 等合法版本标签发布两个包。

在 [Actions](https://github.com/luluxiaoyu/duplicate-file-cleanup-tool-fnos/actions/workflows/dev.yml) 打开成功的 Dev 运行，在 Artifacts 下载 `duplicate-cleanup-amd64` 或 `duplicate-cleanup-arm64`，解压后安装其中的 FPK。也可点击 Run workflow 手动构建。Release 工作流在推送版本标签后构建并发布两种架构的 FPK 和校验文件；应先完成设备验收再创建正式版本标签。

如使用官方 fnpack：将对应 publish 目录内容放入暂存包的 `app/bin/`，前端 dist 放入 `app/www/`，设置对应 platform，再执行 `fnpack build --directory <暂存包目录>`。不要把开发目录直接打包为可运行安装包。

## 数据与恢复

`${TRIM_PKGVAR}/tasks/<任务ID>/task.db` 保存索引、选择草稿、进度、原始空间测量及操作日志，`task.json` 为创建时的描述，数据库是当前状态的权威来源。catalog.db 仅用于用户索引和幂等查询。应用或 NAS 重启后，未完成任务标记中断，手动继续前重新核验权限及文件。

应用回收站位于授权扫描根的 `.duplicate-file-cleanup-tool-fnos-trash/<UID>/<任务ID>/`。临时事务文件位于同根 `.duplicate-file-cleanup-tool-fnos-staging/`。两者排除在扫描之外，每个操作都有恢复清单；不要在操作中删除或改名这些目录。存在异常时原文件保留在暂存位置，任务显示需检查。

回收站和事务目录仅允许应用替换条目，所属 NAS 用户可读取文件。恢复／清空通过任务归属、当前目录授权、文件可读权限和原父目录的写权限校验后由应用完成，避免为了让用户直接删除暂存条目而开放目录写权限。

卸载脚本不遍历或清理外部用户目录。卸载前可先恢复或永久清空回收站，并在应用中心选择保留数据，以便重装后继续查阅记录。不会自动过期任务、自动清空回收站或自动恢复破坏性任务。

## 安全与验收

默认包用户运行，接口仅在 `${TRIM_APPDEST}/app.sock`，不监听公网端口。网关身份与业务 UID 绑定，个人授权、当前 ACL 和路径边界分别检查。只请求 `trim.file.userAccess`、`trim.file.userAcl`、`trim.file.path`；Token 每次从当前进程环境读取，禁止落盘或返回前端。

详细设备验收步骤及当前验证情况见 [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md)。首版最低 API 版本为 fnOS 1.2.0401，宿主 App 1.34.0；是否可上架以 x86 与 ARM 安装运行实测为准，构建通过不代表已经完成设备验收。

启用失败时检查 `/var/apps/duplicate-file-cleanup-tool-fnos/var/lifecycle.log`（启动脚本阶段与进程退出码）和同目录 `server.log`（服务输出）。即使服务尚未输出日志，脚本失败阶段也会写入应用中心临时日志。启动时显式进入安装目录，后端内容根同样固定为安装目录，避免继承不可访问的工作目录。

包图标和入口图标统一从 `scripts/icon.svg` 导出，保留透明边缘和圆角底层。重新导出命令见 `scripts/render_icons.mjs`，不要另用矩形绘图覆盖 SVG 的圆角。

参考：[飞牛开发指南](https://developer.fnnas.com/docs/guide/)、[个人授权 API](https://developer.fnnas.com/api/authorization/user-access/)、[开发 skill](https://github.com/FNOSP/fnos-developer-skill/blob/main/SKILL.md)、[FIDEDUPERANGE](https://man7.org/linux/man-pages/man2/fideduperange.2const.html)、[FIEMAP](https://docs.kernel.org/filesystems/fiemap.html)。
