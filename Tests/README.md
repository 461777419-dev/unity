# 验证脚本

这些文件不需要安装到日常 Unity 工程。

- `Verify-Core.ps1`：PowerShell 7 + Roslyn 核心/静态检查。
- `Verify-UnityCompile.ps1`：复用本机 Unity 引用响应文件，仅执行源码编译。参数为 `-UnityEditorPath`、`-ReferenceResponseFile`、`-OutputDirectory`，可加 `-IncludeValidation` 编译测试入口。
- `UnityEditorValidation*.cs`、`Fixtures/FR2_Cache.cs`：独立工程集成检查入口及测试对象。

## 隔离工程运行

1. 新建一个空的 Unity 2022.3 工程，创建根文件 `organizer-validation-project.marker`。
2. 将本仓库 `Assets/ResourceOrganizer` 复制到测试工程。
3. 将两个 `UnityEditorValidation*.cs` 文件和 `Fixtures/FR2_Cache.cs` 放在测试工程的 `Assets/ResourceOrganizer/Editor` 中。
4. 使用 Unity 参数 `-batchmode -nographics -projectPath <独立工程路径> -executeMethod Suyao.ResourceOrganizer.UnityEditorValidation.Run -logFile <日志路径>` 运行。
5. 查看独立工程根目录 `validation-result.txt`；结尾应为 `Result: PASS`。

每次完整检查使用全新独立工程，避免旧测试资产影响结果。检查会真实修改和删除测试资产。图形预览入口 `ShowUnifiedUiPreview` 仅用于测试后打开真实资源界面，不等于已完成视觉验收。
