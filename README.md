# VirtualCursor — Remote Secondary Cursor Control
VirtualCursor allows you to control an additional on‑screen cursor on a remote Windows machine over the Internet.
The server displays a movable sprite (the “remote cursor”), and a client can move it, click, and even drag objects — while the local (primary) user continues to work with their own physical mouse independently.
All communication is peer‑to‑peer (P2P) via UDP hole punching, with automatic UPnP port forwarding on the server side.

✨ Features
Real‑time remote cursor control – move, click, and drag using the client app.

Simultaneous local and remote interaction – the primary user’s mouse remains fully functional; the remote cursor is rendered as an overlay.

Drag‑and‑drop illusion – remote drag operations temporarily take over the system cursor, giving the impression of two independent dragging actions.

No central server – direct P2P connection after initial signalling (hole punching).

Automatic NAT traversal – uses STUN + UDP hole punching; UPnP is optionally used to open a port on the server side.

Simple signalling – WebSocket‑based signalling server for exchanging public endpoints.

Built with .NET 8 & WPF – modern, cross‑platform ready (Windows only for now).

🧩 How It Works
Server starts, obtains its public IP/port via STUN, and registers with a signalling server.

Client enters the server’s session ID, connects to the signalling server, and requests a connection.

Both peers exchange their public endpoints through the signalling channel.

UDP hole punching establishes a direct P2P channel between client and server.

The client sends movement, click, and drag commands over this UDP channel.

The server interprets these commands, moves the remote cursor sprite, and (when dragging) temporarily takes control of the system cursor to perform actual Windows drag‑and‑drop operations.

🖥️ Requirements
Operating System: Windows 10 / 11 (x64)

Runtime: .NET 8.0

Administrator privileges – required for global mouse hooks (WH_MOUSE_LL) used to track the local mouse independently.

Network: Both sides must have outbound Internet access. The server should allow incoming UDP on a configurable port (default 9050) – UPnP is attempted automatically, but manual port forwarding may be needed if UPnP is not available.

🚀 Getting Started
1. Clone & Build
bash
git clone https://github.com/yourusername/VirtualCursor.git
cd VirtualCursor
dotnet restore
dotnet build -c Release
2. Run the Server
Launch VirtualCursor.Server.exe as Administrator.

The server will display a session ID (e.g., AB3K9L).

It will attempt to forward port 9050 via UPnP and start listening for UDP connections.

Share the session ID with the client user.

3. Run the Client
Launch VirtualCursor.Client.exe.

Enter the server’s session ID and click Connect.

Once connected, a control window appears.

Move your mouse over the control window – the remote cursor on the server follows.

Click and drag to perform remote actions.
