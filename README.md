# PhotosViewer

PhotosViewer 是一款用 C# 和 WPF 开发的 Windows 本地照片浏览与筛选工具。打开照片目录后，可以通过缩略图浏览 JPG 和 JPEG，按文件名搜索、按星级筛选，并按拍摄时间排序。

- 双击缩略图查看大图，支持缩放和原始像素查看。
- 为照片评星或标记为拒绝，评分保存在本机。
- 批量选择照片并移入 Windows 回收站。

在安装了 .NET 10 SDK 的 Windows x64 环境中，从仓库根目录运行：

```powershell
dotnet run --project src/PhotosViewer.App
```
