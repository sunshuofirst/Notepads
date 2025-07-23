# Notepads 项目构建说明

## 项目概述
Notepads 是一个现代、轻量级的文本编辑器，使用 UWP (Universal Windows Platform) 技术开发。

## 当前构建状态
✅ 项目已成功克隆到工作区  
✅ .NET 9.0.303 SDK 已安装并配置  
✅ MAUI Windows 工作负载已安装  
✅ Visual Studio Community 2022 已安装并配置  
✅ 项目构建成功  
✅ MSIX 包已生成

## 构建结果

### 成功构建的组件：
- **Notepads.exe** - 主应用程序可执行文件
- **Notepads.Controls.dll** - 自定义控件库
- **MSIX 安装包** - 多平台安装包：
  - `Notepads_1.5.6.0_x64_Debug.msix` (x64 平台)
  - `Notepads_1.5.6.0_x86_Debug.msix` (x86 平台)
  - `Notepads_1.5.6.0_ARM64_Debug.msix` (ARM64 平台)

### 构建输出位置：
```
src/Notepads/bin/x64/Debug/
├── Notepads.exe                      # 主程序
├── Notepads_1.5.6.0_x64_Debug.msix   # x64 安装包
├── x86/Notepads/                     # x86 版本
├── arm64/Notepads/                   # ARM64 版本
└── 其他依赖文件...
```

## 构建要求

要成功构建此 UWP 项目，您需要安装以下组件：

### 必需组件：
1. **Visual Studio 2022** 或 **Visual Studio Build Tools 2022**
2. **Windows 10 SDK** (版本 10.0.17763.0 或更高)
3. **UWP 开发工具链**

### 推荐的安装方法：

#### 选项 1: 安装 Visual Studio Community 2022（推荐）
1. 从 https://visualstudio.microsoft.com/downloads/ 下载 Visual Studio Community 2022
2. 在安装程序中，选择以下工作负载：
   - **通用 Windows 平台开发**
   - **.NET 桌面开发**
3. 在"单个组件"选项卡中，确保选择：
   - Windows 10 SDK (10.0.22621.0)
   - MSBuild
   - NuGet 包管理器

#### 选项 2: 仅安装 Build Tools（轻量级）
1. 从 https://visualstudio.microsoft.com/downloads/#build-tools-for-visual-studio-2022 下载 Build Tools for Visual Studio 2022
2. 运行安装程序并选择：
   - **C++ 构建工具**
   - **MSBuild 工具**
   - **Windows 10 SDK**

## 构建命令

安装完成后，使用以下命令构建项目：

### 使用 MSBuild（推荐）
```powershell
# 导航到源代码目录
cd src

# 还原 NuGet 包
msbuild Notepads.sln /t:Restore

# 构建解决方案
msbuild Notepads.sln /p:Configuration=Debug /p:Platform=x64
```

### 使用 Visual Studio
1. 打开 `src/Notepads.sln`
2. 选择构建配置（Debug/Release）
3. 选择平台（x64/x86/ARM64）
4. 点击"生成" → "生成解决方案"

### 使用开发者命令提示符（推荐）
1. 搜索并打开 "Developer Command Prompt for VS 2022"
2. 导航到项目目录
3. 运行构建命令

## 项目结构
```
src/
├── Notepads.sln                 # 解决方案文件
├── Notepads/                    # 主应用程序项目
│   └── Notepads.csproj
└── Notepads.Controls/           # 控件库项目
    └── Notepads.Controls.csproj
```

## 运行应用程序

### 注意事项：
- 这是一个 UWP 应用程序，需要通过 MSIX 包安装才能运行
- 未签名的应用需要启用开发者模式或使用测试证书
- 应用依赖于 Microsoft.NET.CoreRuntime.2.2 框架

### 安装方法：
1. **开发环境运行**：通过 Visual Studio 直接运行（F5）
2. **手动安装**：使用 PowerShell 安装 MSIX 包（需要开发者模式）
3. **应用商店**：通过 Microsoft Store 安装正式版本

## 已配置的功能
- Copilot 智能代码补全配置 (`.github/copilot-instructions.md`)
- GitHub Actions CI/CD 工作流
- 依赖机器人配置
- SonarCloud 代码质量分析

## 故障排除

### 常见问题：

1. **"找不到导入的项目...Microsoft.Windows.UI.Xaml.CSharp.targets"**
   - 解决方案：安装 Visual Studio 或 Build Tools 与 UWP 支持

2. **MSBuild 未找到**
   - 解决方案：添加 MSBuild 到 PATH 或使用 Visual Studio Developer Command Prompt

3. **NuGet 包还原失败**
   - 解决方案：确保已安装 NuGet CLI 或使用 Visual Studio 的包管理器

4. **应用安装失败 (0x80073CF3)**
   - 解决方案：启用开发者模式或安装所需的框架依赖

5. **版本冲突警告**
   - 这些是非阻塞性警告，不影响应用功能

## 构建输出说明

### 成功指标：
- ✅ NuGet 包还原完成
- ✅ 编译无错误
- ✅ 生成 MSIX 安装包
- ⚠️ 版本冲突警告（正常，不影响功能）
- ❌ Bundle 创建失败（不影响单个平台包的使用）

### 生成的文件：
- **可执行文件**：每个平台架构的 Notepads.exe
- **安装包**：每个平台架构的 .msix 文件
- **调试符号**：.pdb 文件用于调试
- **资源文件**：.pri 和其他资源文件

## 下一步

项目已成功构建！您现在可以：

1. **开发调试**：使用 Visual Studio 打开解决方案进行开发
2. **功能测试**：通过 Visual Studio 运行应用程序
3. **分发部署**：使用生成的 MSIX 包进行分发

## 相关资源
- [官方文档](README.md)
- [贡献指南](CONTRIBUTING.md)
- [CI/CD 文档](CI-CD_DOCUMENTATION.md)

---

**构建完成日期**: 2025年7月19日  
**构建环境**: Windows + Visual Studio Community 2022  
**目标平台**: UWP (x64, x86, ARM64)
