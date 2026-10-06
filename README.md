# 🚀 Deployment Manager / Release Manager
A modern, high-performance desktop release management and smart deployment tool built with **.NET 8** and **WPF**. Designed for developers and DevOps workflows needing fast, reliable, and reversible deployments across multi-project solutions.

---

## ✨ Key Features

- 🔄 **Smart Differential Sync**: Transfers only new and modified files, significantly reducing deployment time and disk I/O.
- 🛡️ **Automated Backups & Instant Rollback**: Automatically archives overwritten files to a configurable global backup root; roll back an entire deployment or individual projects with a single click.
- 📦 **Publish Delta Package Builder**: Automatically gathers and structures publish outputs modified within the last hour, filtering exclusions (e.g., `*.pdb`, logs) into clean deployable packages.
- 👁️ **Deployment Preview**: Inspect planned changes, file additions, and modifications before executing deployments.
- 📜 **Live Deployment Console & Audit Trail**: Real-time progress tracking, file-by-file log inspection, and resizable console layout.
- 🎨 **Modern Dark UI**: Responsive, enterprise-grade dark aesthetic built with customizable splitters and intuitive project mapping cards.

---

## 🛠️ Tech Stack

- **Framework**: .NET 8 (`net8.0-windows`)
- **UI Platform**: Windows Presentation Foundation (WPF)
- **Architecture**: MVVM with Microsoft Extensions (`Microsoft.Extensions.Hosting`, Dependency Injection, Logging)
- **Runtime**: Windows x64
