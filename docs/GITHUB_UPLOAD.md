# 上传 GitHub

## 上传前

1. 确认仓库中没有真实令牌、个人账号、SSH 私钥和不允许公开的素材。
2. 确认 `git status` 中不包含 `Library`、`.nuget`、`bin`、日志和构建 APK。
3. 决定仓库可见性。许可证未确认前，建议先建立 Private 仓库。
4. 在 GitHub 建立空仓库，不要勾选自动生成 README、`.gitignore` 或 License。

## PowerShell 命令

将 `<你的GitHub仓库地址>` 替换为 GitHub 空仓库页面给出的 HTTPS 地址：

```powershell
cd "D:\Common Application\Users\mydell\Desktop\实习\北大深研院\PICO-RebotArm"
git init
git branch -M main
git add .
git status
git commit -m "Initial PICO reBotArm VR project"
git remote add origin <你的GitHub仓库地址>
git push -u origin main
```

如果 GitHub 要求登录，使用 Git Credential Manager 弹出的浏览器完成授权。不要把个人访问令牌写进脚本或仓库文件。

## 发布给新电脑

源码通过仓库克隆。APK、完整 VMware 虚拟机和其他大体积安装包建议作为 GitHub Release 附件或单独下载文件提供，不要直接放入 Git 历史。

