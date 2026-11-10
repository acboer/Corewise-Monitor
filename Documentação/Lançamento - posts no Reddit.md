# Lançamento menor (preview, antes da assinatura)

Ordem sugerida: r/SideProject → r/alphaandbetausers → r/homelab (um por dia, para conseguir responder tudo).
Antes de postar: leia as regras de cada comunidade (autopromoção, flair obrigatório, dia da semana permitido) e
deixe o link do VirusTotal pronto nas notas da Release.

Imagens: 3–4 capturas em inglês (visão geral com 2+ PCs, widget, overlay de FPS num jogo, histórico de um jogo).
O overlay é o que mais chama atenção: capture num jogo em tela cheia.

---

## r/SideProject

**Título:** I built a free, open-source hardware monitor that watches all my PCs (gaming rig + NAS) from one dashboard

**Texto:**

Hi! I've been building **Corewise** for a few weeks as a personal project, and it just reached its first public preview.

**The problem:** I have a workstation and a NAS, and I wanted one place to see temperatures, disk health, fans and network for both, plus an FPS overlay for games, without remote-desktopping into the NAS or running three different tools.

**What it does:**
- A small Windows service on each PC reads the sensors (CPU, GPU, RAM, disks with SMART, fans, power, network, even headset/mouse battery).
- One dashboard and a desktop widget show all your PCs. Adding a PC = type its name, click **Request access**, click **Allow** on the other PC.
- In-game FPS / 1 % low / frame-time overlay, read from Windows events (nothing injected, so no anti-cheat issues).
- Up to 2 years of history, game sessions with FPS and temperatures, and benchmarks (disk, network between two PCs, CPU, memory, GPU, stability test).
- Everything stays on your LAN: no accounts, no telemetry, no internet.

**Stack:** C# / .NET 10, WPF, ASP.NET Core minimal API (HTTPS with pinned certificates), SQLite, LibreHardwareMonitor for sensors, Inno Setup for the installer. Built and released through GitHub Actions. MIT license.

**Honest caveat:** the installer isn't code-signed yet (I've applied to SignPath Foundation's free signing for open-source projects), so Windows SmartScreen warns on install. The source and the build workflow are public if you want to check what it does.

Repo and download: https://github.com/acboer/Corewise-Monitor

I'd love feedback on the UX, on what's missing compared to the tools you use today, and bug reports if you try it. Thanks!

---

## r/alphaandbetausers

**Título:** [Windows] Corewise: looking for testers for a free hardware monitor for multiple PCs (sensors, FPS overlay, benchmarks)

**Texto:**

**What it is:** Corewise is a free, open-source (MIT) hardware monitor for Windows 10/11. It watches your own PC and the other PCs on your network (NAS, second PC, render nodes) from one dashboard and a desktop widget.

**What I'd like you to test:**
1. Install (Full on your main PC; "Service only" on a second PC if you have one).
2. Add the second PC: **Add machine** → its name → **Find** → **Request access**, then **Allow** on the other PC.
3. Play a game for a few minutes with the FPS overlay (Ctrl+Shift+F10) and check **History → Games**.
4. Run one benchmark (CPU or disk) and tell me your score and hardware.

**What I need back:** anything confusing, wrong sensor values (please include your CPU/GPU/motherboard), crashes (the log is at `%AppData%\Corewise\erro.log`), and features you'd expect.

**Good to know:**
- Preview, not code-signed yet (SignPath application in progress): SmartScreen will warn ("More info → Run anyway"). If Windows **Smart App Control** is on, it will block it; please skip this round in that case.
- The installer adds a Windows service (needed to read sensors as administrator) and a firewall rule for its port on private networks only. No telemetry, nothing leaves your network.

Download: https://github.com/acboer/Corewise-Monitor/releases
Issues/feedback: https://github.com/acboer/Corewise-Monitor/issues (or reply here)

---

## r/homelab

**Título:** Made a free, open-source monitor for my Windows NAS and gaming PC: SMART, temps, network and disk benchmarks from one dashboard

**Texto:**

My homelab is small (a Windows box as NAS + my workstation), and I got tired of RDP-ing into the NAS to check disk temperatures and SMART, so I built **Corewise**.

- A lightweight service on each Windows machine ("Service only" install, no UI on the NAS) reads CPU, disks (SMART health, wear, temperatures without waking sleeping HDDs), volumes, fans, network and power.
- One dashboard on your desktop shows every machine, with yellow/red warnings for hot or failing disks and full volumes, and up to 2 years of history.
- Network speed test between two machines over the same HTTPS port (no iperf server to set up), plus a CrystalDiskMark-style disk test that runs on the remote box.
- Silent install for several machines: `CorewiseSetup.exe /VERYSILENT /TYPE=servico`.
- LAN only: HTTPS with per-machine certificates pinned at pairing, firewall rule for private networks, no cloud, no telemetry.

Windows only for now (it relies on Windows sensor and event APIs). It's an early preview and not code-signed yet (applied to SignPath Foundation), so SmartScreen will complain. Source and build pipeline are on GitHub (MIT):

https://github.com/acboer/Corewise-Monitor

Would love to hear what you'd want from it on a NAS/server: alerts by e-mail? A web view for your phone? Linux agent?

---

## Respostas prontas para perguntas prováveis

**"Why does it need a service running as SYSTEM?"**
Reading motherboard/CPU sensors (through LibreHardwareMonitor and the PawnIO driver), SMART data and the frame events used by the FPS overlay all require administrator rights. Splitting it into a service lets the dashboard and widget run as a normal user. The service only listens on your LAN, on a private-network firewall rule, and only answers dashboards you paired.

**"How is this different from HWiNFO / Afterburner / LibreHardwareMonitor?"**
Those are great on one PC; Corewise is about several PCs at once (NAS, render nodes, second PC) from one dashboard and widget, plus history and benchmarks. It uses LibreHardwareMonitor's library for part of the sensor reading.

**"Is it safe? It's unsigned."**
Fair concern. The source is public, the installer is built by GitHub Actions from that source (the workflow is in the repo), and here's the VirusTotal scan: <link>. Signing through SignPath Foundation is in progress.

**"Linux/macOS?"**
Not now: it depends on Windows APIs for sensors and FPS. A web view of the dashboard (for phones and other OSes) is on the idea list.
