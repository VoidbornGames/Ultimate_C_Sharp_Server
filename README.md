# UltimateServer

A lightweight, high-performance **C# TCP server** for managing users, commands, and real-time communication. Designed for scalability and easy integration with clients or game servers.

Check out the live dashboard here: # [Live Dashboard](http://myt.voidborn-games.ir:11002/)

---

## **Table of Contents**

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Usage](#usage)
- [Commands](#commands)
- [Configuration](#configuration)
- [Logging](#logging)
- [License](#license)

---

## **Features**

- TCP-based server with **async client handling**.
- User management:
  - Create, list, and login users.
  - Role-based system (`player` by default).
- Command handling:
  - Custom commands like `say`, `makeUUID`, `stats`.
- Configurable **IP, port, and max connections**.
- Auto-save users every 15 minutes.
- Automatic log rotation with ZIP compression.
- Supports **multi-client concurrency** with `ConcurrentDictionary`.

---

## **Requirements**

- .NET 6.0 SDK or Runtime
- Windows, Linux, or Docker-compatible environment
- Ports open for TCP connections (default: `11001`)

---

## **Installation**

1. **Clone the repository**:
```bash
git clone https://github.com/VoidbornGames/Ultimate_C_Sharp_Server.git
cd Ultimate_C_Sharp_Server
```

## **Running it**

- Default:
```bash
dotnet Server.dll 11001
```

- Custom:
```bash
dotnet Server.dll OPEN_PORT
```
