# 🚀 MikroTik ROS Local Update Server WIP

<div align="center">

![Version](https://img.shields.io/badge/version-1.0.0-blue)
![\.NET](https://img.shields.io/badge/.NET-9.0-purple)
![License](https://img.shields.io/badge/license-ELv2-orange)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-lightgrey)

**Local update server for MikroTik RouterOS devices**

_Cache firmware, manage versions and update devices without Internet access_

</div>

## ✨ Features

- 🔧 Full local emulation of MikroTik update servers  
- 📦 Version management for RouterOS v6 and v7 with multiple architectures support  
- 🧩 Configurable *Allowed Arches* via web UI  
- 🕐 Automatic scheduled check for new updates  
- 📊 Interactive dashboard:
  - average firmware download speed
  - time of last update check
  - days without failures (uptime)
  - average version size
- 🚨 Real-time alerts:
  - low free disk space
  - Internet connectivity problems
  - overall update service health
- 📋 Detailed logs and built-in log viewer
- 📈 Basic log analytics (counts by level, activity over time)
- 🌗 Switchable dark & light themes with saved preference
- 🔒 Basic HTTP security headers hardening
- ⚡ High performance: response compression and caching

## 🚀 Quick Start

### Requirements

- .NET 9.0 Runtime or newer  
- 500 MB free disk space  
- Internet access for initial firmware download  
- Modern browser for the web interface  

### Installation

1. Download the latest release archive from the Releases section of your repository  
2. Extract it to any directory  
3. Run the application:
   - Windows: `MikroTik.UpdateServer.exe`
   - Linux: `./MikroTik.UpdateServer`
4. Open your browser at `http://localhost:5000`

## ⚙️ MikroTik configuration

Add a DNS record on your MikroTik device:

```routeros
/ip dns static add name="upgrade.mikrotik.com" address=<IP_OF_YOUR_SERVER>
```

Check for updates:

```routeros
/system package update check-for-updates
/system package update install
```

## 🎯 Usage

### 📊 Dashboard

The main dashboard provides:

- Quick Stats:
  - average firmware download speed
  - last update check time
  - days without failures
  - average size of locally stored versions
- Server status:
  - Internet connectivity
  - free disk space estimate
  - overall service status
- Theme switcher (🌙 / ☀️ icon in the left sidebar)

You can also trigger a manual update check from the dashboard.

### 📦 Version Management

The Versions section allows you to:

- Browse available RouterOS v6 and v7 versions  
- See size and download date for each version  
- Search and sort by name, date, and size  
- Mark favorite versions for quick access  
- Set the active version that devices will receive  
- Remove obsolete or unused builds  

Allowed architectures (*Allowed Arches*) are configured separately to control which device types can use this server.

### 📅 Scheduler

The scheduler is responsible for automatic checks of new RouterOS versions:

- Set check interval (in minutes)  
- Bind check time to server time  
- Temporarily pause / resume auto checks  
- Display current scheduler status (active / paused)  

### 📋 Logs & Diagnostics

Logging subsystem includes:

- Built-in log viewer in the web UI  
- Filtering by level (Debug / Info / Warning / Error)  
- Text search in log messages  
- Export logs as ZIP archive  
- Basic analytics:
  - number of entries by level
  - activity over time

## 🌗 UI Themes

The UI supports dark and light themes.

- Switch via button in the left sidebar  
- Choice is stored in browser Local Storage  
- On first launch, system preference (`prefers-color-scheme`) is used, then the explicit user choice wins  

## 🔧 API Endpoints

| Method | Endpoint                            | Description                                  |
|--------|-------------------------------------|----------------------------------------------|
| GET    | `/api/dashboard`                    | Dashboard summary statistics                  |
| GET    | `/api/versions`                     | List of available versions                    |
| POST   | `/api/update-check`                 | Trigger update check                          |
| POST   | `/api/set-active-version/{version}` | Set active version                            |
| GET    | `/api/status`                       | Current server status                         |
| GET    | `/api/logs`                         | Log viewer                                    |
| GET    | `/api/logs/stats`                   | Aggregated log statistics                     |
| GET    | `/api/allowed-arches`               | Get allowed architectures settings            |
| POST   | `/api/allowed-arches`               | Save allowed architectures settings           |

## 🛠️ Development

```bash
git clone https://github.com/Shepard199/mikrotik-update-server.git
cd mikrotik-update-server
dotnet restore
dotnet build
dotnet run
```

### Configuration

```json
{
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:5000"
      }
    }
  }
}
```

## ⚠️ Disclaimer

This project is not affiliated with MikroTik.  
Use at your own risk.  
The author is not responsible for any issues with your devices.

## 📄 Licensing

(dual licensing: GPLv3 or commercial, as in main README)
