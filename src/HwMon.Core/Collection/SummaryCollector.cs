using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using HwMon.Core.Telemetry;

namespace HwMon.Core.Collection;

/// <summary>
/// Resumo do sistema (estilo CPU-Z): só informação estática, lida do WMI/SMBIOS, do registro e do Windows.
/// Não acorda HDDs (não lê SMART; só o catálogo de discos, que já é lido na descoberta). Leva 1–3 s.
/// Cada fonte é independente: uma falha vira uma linha em Problems e o resto continua.
/// </summary>
public static class SummaryCollector
{
    /// <summary>Cultura do idioma das mensagens (o nome ficou da época em que era só português).</summary>
    private static CultureInfo Pt => L.Culture;

    public static SystemSummary Collect()
    {
        var problems = new List<string>();
        var summary = new SystemSummary { HostName = Environment.MachineName, CollectedAt = DateTimeOffset.Now, Problems = problems };
        void Add(Func<List<string>, SummarySection?> build, string name)
        {
            try
            {
                if (build(problems) is { } s && s.Groups.Any(g => g.Items.Count > 0)) summary.Sections.Add(s);
            }
            catch (Exception ex)
            {
                problems.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Add(OperatingSystem, "sistema");
        Add(Board, "placa-mãe");
        Add(Processor, "processador");
        Add(Memory, "memória");
        Add(Graphics, "vídeo");
        Add(Storage, "armazenamento");
        Add(Network, "rede");
        Add(Displays, "monitores");
        Add(Audio, "áudio");
        Add(Power, "energia");
        return summary;
    }

    // ------------------------------------------------------------------ helpers

    private sealed class Lines : List<SummaryItem>
    {
        public void Add(string label, string? value)
        {
            // Rótulos e valores fixos ("Ligada", "Não") no idioma do serviço; nomes de hardware não estão no dicionário.
            if (!string.IsNullOrWhiteSpace(value)) base.Add(new SummaryItem(L.T(label), L.T(value.Trim())));
        }
    }

    private static SummarySection Section(string id, string title, string? headline, params (string? Title, Lines Items)[] groups) => new()
    {
        Id = id,
        Title = L.T(title),
        Headline = headline,
        Groups = groups.Where(g => g.Items.Count > 0).Select(g => new SummaryGroup { Title = g.Title is null ? null : L.T(g.Title), Items = g.Items }).ToList(),
    };

    private static void Try(List<string> problems, string what, Action action) => SystemCollector.Try(problems, what, action);

    private static ManagementObject? First(string ns, string wql) => Wmi.Query(ns, wql).FirstOrDefault();

    private static DateTime? CimDate(ManagementBaseObject o, string property)
    {
        var s = Wmi.Str(o, property);
        if (s is null) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); }
        catch { return null; }
    }

    private static string Date(DateTime? d) => d?.ToString(L.English ? "MM/dd/yyyy" : "dd/MM/yyyy", Pt) ?? "";
    private static string Gb(double bytes) => bytes >= 1e12 ? $"{(bytes / 1e12).ToString("0.##", Pt)} TB" : $"{(bytes / 1e9).ToString("0.#", Pt)} GB";
    private static string GiB(double bytes) => $"{(bytes / 1_073_741_824.0).ToString("0.#", Pt)} GB";
    private static string Size(uint bytes) => bytes >= 1_048_576 ? $"{(bytes / 1_048_576.0).ToString("0.##", Pt)} MB" : $"{bytes / 1024} KB";
    private static string YesNo(bool? v) => v switch { true => "Sim", false => "Não", null => "" };

    private static string PcieText(Native.PcieLink? link)
    {
        if (link is null) return "";
        static string Gen(int g) => g switch { 1 => "1.1", 2 => "2.0", 3 => "3.0", 4 => "4.0", 5 => "5.0", 6 => "6.0", _ => "?" };
        var max = $"PCIe {Gen(link.MaxGen)} x{link.MaxWidth}";
        if (link.CurrentGen == 0 || (link.CurrentGen == link.MaxGen && link.CurrentWidth == link.MaxWidth)) return max;
        return $"{max} (agora {Gen(link.CurrentGen)} x{link.CurrentWidth}{(link.CurrentGen < link.MaxGen && link.CurrentWidth == link.MaxWidth ? ", economia de energia" : "")})";
    }

    // ------------------------------------------------------------------ sistema operacional e segurança

    private static SummarySection OperatingSystem(List<string> problems)
    {
        var os = new Lines();
        var security = new Lines();
        string? headline = null;
        Try(problems, "Win32_OperatingSystem", () =>
        {
            var o = First(Wmi.Cimv2, "SELECT Caption, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem");
            if (o is null) return;
            using var cv = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var display = cv?.GetValue("DisplayVersion") as string;
            var build = $"{Wmi.Str(o, "BuildNumber")}.{cv?.GetValue("UBR")}";
            headline = $"{Wmi.Str(o, "Caption")?.Replace("Microsoft ", "")} {display}".Trim();
            os.Add("Edição", Wmi.Str(o, "Caption")?.Replace("Microsoft ", ""));
            os.Add("Versão", display);
            os.Add("Build", build);
            os.Add("Arquitetura", Wmi.Str(o, "OSArchitecture"));
            os.Add("Instalado em", Date(CimDate(o, "InstallDate")));
            if (CimDate(o, "LastBootUpTime") is { } boot)
            {
                var up = DateTime.Now - boot;
                os.Add("Ligado desde", $"{boot.ToString(L.English ? "MM/dd/yyyy HH:mm" : "dd/MM/yyyy HH:mm", Pt)} ({(up.TotalDays >= 1 ? $"{(int)up.TotalDays} d " : "")}{up.Hours} h {up.Minutes} min)");
            }
        });
        Try(problems, "Win32_ComputerSystem", () =>
        {
            var cs = First(Wmi.Cimv2, "SELECT Name, Domain, PartOfDomain, Workgroup, HypervisorPresent FROM Win32_ComputerSystem");
            if (cs is null) return;
            os.Add("Nome do PC", Wmi.Str(cs, "Name"));
            os.Add(Wmi.Get<bool?>(cs, "PartOfDomain") == true ? "Domínio" : "Grupo de trabalho",
                Wmi.Get<bool?>(cs, "PartOfDomain") == true ? Wmi.Str(cs, "Domain") : Wmi.Str(cs, "Workgroup"));
            security.Add("Hipervisor ativo", YesNo(Wmi.Get<bool?>(cs, "HypervisorPresent")));
        });

        security.Add("Firmware", Native.FirmwareType());
        Try(problems, "Secure Boot", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            if (key?.GetValue("UEFISecureBootEnabled") is int sb) security.Add("Secure Boot", sb == 1 ? "Ligado" : "Desligado");
        });
        Try(problems, "TPM", () =>
        {
            var tpm = First(@"root\cimv2\Security\MicrosoftTpm", "SELECT SpecVersion, ManufacturerIdTxt, ManufacturerVersion, IsEnabled_InitialValue FROM Win32_Tpm");
            if (tpm is null)
            {
                security.Add("TPM", "Não encontrado");
                return;
            }
            var spec = Wmi.Str(tpm, "SpecVersion")?.Split(',')[0].Trim();
            var on = Wmi.Get<bool?>(tpm, "IsEnabled_InitialValue");
            security.Add("TPM", $"{spec} · {Wmi.Str(tpm, "ManufacturerIdTxt")} {Wmi.Str(tpm, "ManufacturerVersion")}{(on == false ? " · desativado" : "")}");
        });
        Try(problems, "DeviceGuard", () =>
        {
            var dg = First(Wmi.DeviceGuard, "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
            if (dg is null) return;
            var running = Wmi.Get<uint[]>(dg, "SecurityServicesRunning") ?? [];
            security.Add("Integridade de memória (HVCI)", running.Contains(2u) ? "Ligada" : "Desligada");
        });

        return Section("os", "Sistema operacional", headline, (null, os), ("Segurança", security));
    }

    // ------------------------------------------------------------------ placa-mãe e BIOS

    private static SummarySection Board(List<string> problems)
    {
        var board = new Lines();
        var bios = new Lines();
        string? headline = null;
        Try(problems, "Win32_BaseBoard", () =>
        {
            var bb = First(Wmi.Cimv2, "SELECT Manufacturer, Product, Version FROM Win32_BaseBoard");
            if (bb is null) return;
            headline = $"{Wmi.Str(bb, "Manufacturer")} {Wmi.Str(bb, "Product")}".Trim();
            board.Add("Fabricante", Wmi.Str(bb, "Manufacturer"));
            board.Add("Modelo", Wmi.Str(bb, "Product"));
            var rev = Wmi.Str(bb, "Version");
            if (rev is not null && !rev.Contains("filled", StringComparison.OrdinalIgnoreCase) && rev != "Default string") board.Add("Revisão", rev);
            board.Add("Site", VendorSites.For(Wmi.Str(bb, "Manufacturer"), Wmi.Str(bb, "Product")));
        });
        Try(problems, "Win32_ComputerSystem", () =>
        {
            var cs = First(Wmi.Cimv2, "SELECT Manufacturer, Model, SystemSKUNumber FROM Win32_ComputerSystem");
            if (cs is null) return;
            static bool Useful(string? s) => s is not null && !Regex.IsMatch(s, "filled|default string|system product name|system manufacturer|^to be", RegexOptions.IgnoreCase);
            var model = Wmi.Str(cs, "Model");
            if (Useful(model) && headline?.Contains(model!, StringComparison.OrdinalIgnoreCase) != true)
                board.Add("Sistema", $"{Wmi.Str(cs, "Manufacturer")} {model}");
        });
        Try(problems, "Win32_BIOS", () =>
        {
            var b = First(Wmi.Cimv2, "SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate, SMBIOSMajorVersion, SMBIOSMinorVersion FROM Win32_BIOS");
            if (b is null) return;
            bios.Add("Fabricante", Wmi.Str(b, "Manufacturer"));
            bios.Add("Versão", Wmi.Str(b, "SMBIOSBIOSVersion"));
            bios.Add("Data", Date(CimDate(b, "ReleaseDate")));
            if (Wmi.Get<int?>(b, "SMBIOSMajorVersion") is { } major) bios.Add("SMBIOS", $"{major}.{Wmi.Get<int?>(b, "SMBIOSMinorVersion")}");
        });
        bios.Add("Modo", Native.FirmwareType());
        return Section("board", "Placa-mãe e BIOS", headline, (null, board), ("BIOS", bios));
    }

    // ------------------------------------------------------------------ processador

    private static SummarySection Processor(List<string> problems)
    {
        var cpu = new Lines();
        var cache = new Lines();
        string? headline = null;
        Try(problems, "Win32_Processor", () =>
        {
            var p = First(Wmi.Cimv2, "SELECT Name, Manufacturer, Caption, SocketDesignation, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, VirtualizationFirmwareEnabled FROM Win32_Processor");
            if (p is null) return;
            var name = Regex.Replace((Wmi.Str(p, "Name") ?? "").Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", ""), @"\s{2,}", " ").Trim();
            var cores = Wmi.Get<int?>(p, "NumberOfCores");
            var threads = Wmi.Get<int?>(p, "NumberOfLogicalProcessors");
            headline = L.T("{0} · {1} núcleos · {2} threads", name, cores, threads);
            cpu.Add("Nome", name);
            cpu.Add("Fabricante", Wmi.Str(p, "Manufacturer") switch { "AuthenticAMD" => "AMD", "GenuineIntel" => "Intel", var m => m });
            cpu.Add("Site", VendorSites.For(Wmi.Str(p, "Manufacturer"), name));
            var caption = Wmi.Str(p, "Caption");
            var m2 = caption is null ? null : Regex.Match(caption, @"Family (\d+) Model (\d+) Stepping (\d+)");
            if (m2 is { Success: true })
            {
                int F(int g) => int.Parse(m2.Groups[g].Value, CultureInfo.InvariantCulture);
                cpu.Add("Família · modelo · stepping", $"{F(1)} ({F(1):X}h) · {F(2)} ({F(2):X}h) · {F(3)}");
            }
            // Algumas placas (ASRock) preenchem só "CPUSocket": não diz nada, fica de fora.
            var socket = Wmi.Str(p, "SocketDesignation");
            if (socket is not null && !Regex.IsMatch(socket, @"^(CPU ?Socket|Socket ?\d*|U3E1|CPU ?\d*)$", RegexOptions.IgnoreCase)) cpu.Add("Soquete", socket);

            var (coreList, caches) = Native.ProcessorTopology();
            var classes = coreList.Select(c => c.EfficiencyClass).Distinct().OrderByDescending(c => c).ToList();
            if (classes.Count > 1)
            {
                var pCores = coreList.Count(c => c.EfficiencyClass == classes[0]);
                var eCores = coreList.Count - pCores;
                cpu.Add("Núcleos", $"{coreList.Count} ({pCores} P + {eCores} E)");
            }
            else
            {
                cpu.Add("Núcleos", cores?.ToString(CultureInfo.InvariantCulture));
            }
            cpu.Add("Threads", threads?.ToString(CultureInfo.InvariantCulture));
            if (Wmi.Get<int?>(p, "MaxClockSpeed") is { } mhz) cpu.Add("Clock base", $"{(mhz / 1000.0).ToString("0.00", Pt)} GHz");

            foreach (var g in caches.GroupBy(c => (c.Level, c.Type)).OrderBy(g => g.Key.Level).ThenByDescending(g => g.Key.Type))
            {
                var label = $"L{g.Key.Level}" + g.Key.Type switch { 1 => " " + L.T("instruções"), 2 => " " + L.T("dados"), _ => "" };
                var sizes = g.GroupBy(c => c.SizeBytes).Select(s => $"{s.Count()} × {Size(s.Key)}");
                cache.Add(label, $"{string.Join(" + ", sizes)}  (total {Size((uint)g.Sum(c => (long)c.SizeBytes))})");
            }

            cpu.Add("Virtualização no firmware", YesNo(Wmi.Get<bool?>(p, "VirtualizationFirmwareEnabled")) switch
            {
                "Não" => "Desligada ou em uso pelo Hyper-V",
                "Sim" => "Ligada",
                var x => x,
            });
        });
        Try(problems, "microcódigo", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("Update Revision") is byte[] { Length: >= 8 } rev)
            {
                var high = BitConverter.ToUInt32(rev, 4);
                var low = BitConverter.ToUInt32(rev, 0);
                var value = high != 0 ? high : low;
                if (value != 0) cpu.Add("Microcódigo", $"0x{value:X}");
            }
        });
        cpu.Add("Instruções", Instructions());
        return Section("cpu", "Processador", headline, (null, cpu), ("Cache", cache));
    }

    private static string Instructions()
    {
        var list = new List<string>();
        void Has(bool ok, string name) { if (ok) list.Add(name); }
        Has(Sse42.IsSupported, "SSE4.2");
        Has(Avx.IsSupported, "AVX");
        Has(Avx2.IsSupported, "AVX2");
        Has(Fma.IsSupported, "FMA3");
        Has(Avx512F.IsSupported, "AVX-512");
        Has(AvxVnni.IsSupported, "AVX-VNNI");
        Has(System.Runtime.Intrinsics.X86.Aes.IsSupported, "AES");
        Has(Bmi2.IsSupported, "BMI2");
        Has(Popcnt.IsSupported, "POPCNT");
        return string.Join(", ", list);
    }

    // ------------------------------------------------------------------ memória

    private static SummarySection Memory(List<string> problems)
    {
        var overview = new Lines();
        var groups = new List<(string?, Lines)>();
        string? headline = null;
        Try(problems, "Win32_PhysicalMemory", () =>
        {
            var arrays = Wmi.Query(Wmi.Cimv2, "SELECT MemoryDevices, MaxCapacityEx FROM Win32_PhysicalMemoryArray");
            var slots = arrays.Sum(a => Wmi.Get<int?>(a, "MemoryDevices") ?? 0);
            var maxKb = arrays.Sum(a => (double)(Wmi.Get<ulong?>(a, "MaxCapacityEx") ?? 0));
            var modules = Wmi.Query(Wmi.Cimv2, "SELECT DeviceLocator, BankLabel, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType, FormFactor, ConfiguredVoltage, Attributes FROM Win32_PhysicalMemory");
            var total = modules.Sum(m => (double)(Wmi.Get<ulong?>(m, "Capacity") ?? 0));
            string? Type(ManagementObject m) => Wmi.Get<int?>(m, "SMBIOSMemoryType") switch { 26 => "DDR4", 34 => "DDR5", 24 => "DDR3", 35 => "LPDDR5", 30 => "LPDDR4", _ => null };
            var type = modules.Select(Type).FirstOrDefault(t => t is not null);
            var speed = modules.Select(m => Wmi.Get<int?>(m, "ConfiguredClockSpeed")).FirstOrDefault(s => s > 0);
            headline = $"{GiB(total)} {type} {speed} MT/s · {modules.Count} {(modules.Count == 1 ? "pente" : "pentes")}".Replace("  ", " ");
            overview.Add("Total", GiB(total));
            overview.Add("Tipo", type);
            if (slots > 0) overview.Add("Slots", L.T("{0} de {1} ocupados", modules.Count, slots));
            if (maxKb > 0) overview.Add("Máximo suportado", GiB(maxKb * 1024));
            overview.Add("Canais", modules.Count switch { >= 2 when modules.Count % 2 == 0 => "Dual channel (provável)", 1 => "Single channel", _ => null });

            foreach (var m in modules.OrderBy(m => Wmi.Str(m, "DeviceLocator")))
            {
                var g = new Lines();
                g.Add("Capacidade", GiB(Wmi.Get<ulong?>(m, "Capacity") ?? 0));
                var part = Wmi.Str(m, "PartNumber");
                var vendor = RamVendor(Wmi.Str(m, "Manufacturer"), part);
                g.Add("Fabricante", vendor);
                g.Add("Part number", part);
                g.Add("Site", VendorSites.For(vendor ?? part, part));
                var nominal = Wmi.Get<int?>(m, "Speed");
                var configured = Wmi.Get<int?>(m, "ConfiguredClockSpeed");
                g.Add("Velocidade", configured is > 0 && nominal is > 0 && configured != nominal
                    ? $"{configured} MT/s (nominal {nominal} MT/s)" : $"{configured ?? nominal} MT/s");
                g.Add("Tipo", $"{Type(m)} {Wmi.Get<int?>(m, "FormFactor") switch { 8 => "DIMM", 12 => "SO-DIMM", _ => "" }}");
                if (Wmi.Get<int?>(m, "ConfiguredVoltage") is { } mv and > 0) g.Add("Tensão", $"{(mv / 1000.0).ToString("0.00#", Pt)} V");
                if (Wmi.Get<int?>(m, "Attributes") is { } rank and > 0) g.Add("Ranks", rank.ToString(CultureInfo.InvariantCulture));
                groups.Add((Wmi.Str(m, "DeviceLocator") ?? Wmi.Str(m, "BankLabel") ?? "Pente", g));
            }
        });
        overview.Add("Timings (CL, tRCD…)", "Não disponíveis (o Windows bloqueia a leitura do SPD)");
        return Section("memory", "Memória", headline, [(null, overview), .. groups]);
    }

    /// <summary>
    /// O SMBIOS costuma dizer "Unknown" ou um código JEDEC; o prefixo do part number identifica a marca dos módulos
    /// de varejo (Corsair CMK/CMH…, G.Skill F4-/F5-, Kingston KF/KVR…).
    /// </summary>
    private static string? RamVendor(string? smbios, string? part)
    {
        var known = smbios is not null && !Regex.IsMatch(smbios, @"^(unknown|undefined|0x|[0-9A-F]{4,}$)", RegexOptions.IgnoreCase);
        if (known) return smbios;
        if (part is null) return null;
        (string Pattern, string Name)[] prefixes =
        [
            (@"^CM[A-Z]", "Corsair"), (@"^F[3-5]-", "G.Skill"), (@"^(KF|KVR|KHX|KCP)", "Kingston"), (@"^(BL|CT)\d", "Crucial"),
            (@"^M(3|4)\d\d", "Samsung"), (@"^HM[A-Z]", "SK hynix"), (@"^MTA", "Micron"), (@"^(TF|TD|TED|FF)\d", "TeamGroup"),
            (@"^PV[ES]", "Patriot"), (@"^AX[45]", "ADATA XPG"), (@"^AD[45]", "ADATA"), (@"^PSD", "Patriot"),
        ];
        foreach (var (pattern, name) in prefixes)
            if (Regex.IsMatch(part, pattern, RegexOptions.IgnoreCase)) return L.T("{0} (pelo part number)", name);
        return null;
    }

    // ------------------------------------------------------------------ placa de vídeo

    private static readonly Dictionary<string, string> PciVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["10DE"] = "NVIDIA", ["1002"] = "AMD", ["8086"] = "Intel", ["1043"] = "ASUS", ["1462"] = "MSI", ["1458"] = "Gigabyte",
        ["3842"] = "EVGA", ["19DA"] = "Zotac", ["1569"] = "Palit", ["1DA2"] = "Sapphire", ["148C"] = "PowerColor",
        ["1682"] = "XFX", ["7377"] = "Colorful", ["196E"] = "PNY", ["1849"] = "ASRock", ["1B4C"] = "Galax/KFA2",
        ["17AA"] = "Lenovo", ["1028"] = "Dell", ["103C"] = "HP", ["10B0"] = "Gainward", ["174B"] = "Sapphire (PC Partner)",
        ["1025"] = "Acer", ["1179"] = "Toshiba", ["144D"] = "Samsung",
    };

    private static SummarySection Graphics(List<string> problems)
    {
        var groups = new List<(string?, Lines)>();
        var names = new List<string>();
        Try(problems, "Win32_VideoController", () =>
        {
            foreach (var v in Wmi.Query(Wmi.Cimv2, "SELECT Name, PNPDeviceID, AdapterCompatibility, DriverVersion, DriverDate, VideoProcessor FROM Win32_VideoController"))
            {
                var pnp = Wmi.Str(v, "PNPDeviceID");
                if (pnp is null || !pnp.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) continue; // adaptadores virtuais/remotos
                var name = Wmi.Str(v, "Name") ?? "Placa de vídeo";
                names.Add(name);
                var g = new Lines();
                var ids = Regex.Match(pnp, @"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})&SUBSYS_([0-9A-F]{4})([0-9A-F]{4})", RegexOptions.IgnoreCase);
                var chipVendor = ids.Success ? PciVendors.GetValueOrDefault(ids.Groups[1].Value) : null;
                var processor = Wmi.Str(v, "VideoProcessor") ?? name;
                var maker = chipVendor ?? Wmi.Str(v, "AdapterCompatibility");
                g.Add("Chip", maker is null || processor.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? processor : $"{maker} {processor}");
                if (ids.Success && PciVendors.TryGetValue(ids.Groups[4].Value, out var board) && board != chipVendor) g.Add("Fabricante da placa", board);

                // Chave do driver: VRAM de 64 bits e versão da VBIOS (o AdapterRAM do WMI para em 4 GB).
                using (var enumKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{pnp}"))
                using (var classKey = enumKey?.GetValue("Driver") is string driver ? Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driver}") : null)
                {
                    if (classKey?.GetValue("HardwareInformation.qwMemorySize") is long vram and > 0) g.Add("Memória de vídeo", GiB(vram));
                    else if (classKey?.GetValue("HardwareInformation.MemorySize") is byte[] { Length: >= 4 } ms) g.Add("Memória de vídeo", GiB(BitConverter.ToUInt32(ms, 0)));
                    var biosString = classKey?.GetValue("HardwareInformation.BiosString") switch
                    {
                        byte[] bytes => Encoding.Unicode.GetString(bytes).TrimEnd('\0'),
                        string s => s,
                        _ => null,
                    };
                    g.Add("VBIOS", biosString is null ? null : Regex.Replace(biosString, @"^Version\s*", "", RegexOptions.IgnoreCase));
                }

                var driverVersion = Wmi.Str(v, "DriverVersion");
                if (chipVendor == "NVIDIA" && driverVersion is not null)
                {
                    // 32.0.15.8180 → 581.80 (os 5 últimos dígitos são a versão que a NVIDIA divulga)
                    var digits = driverVersion.Replace(".", "");
                    if (digits.Length >= 5) driverVersion = $"{digits[^5..^2]}.{digits[^2..]} ({driverVersion})";
                }
                g.Add("Driver", driverVersion);
                g.Add("Data do driver", Date(CimDate(v, "DriverDate")));
                g.Add("Barramento", PcieText(Native.GetPcieLink(pnp)));
                // GPU integrada: o "fabricante da placa" é a própria placa-mãe; o link ficaria enganoso.
                var integrated = Regex.IsMatch(name, @"UHD|Iris|Radeon\(TM\) Graphics|Radeon Graphics|Vega \d+ Graphics", RegexOptions.IgnoreCase);
                if (!integrated && ids.Success && PciVendors.TryGetValue(ids.Groups[4].Value, out var maker2) && maker2 != chipVendor)
                    g.Add("Site da placa", VendorSites.For(maker2, name));
                g.Add("Drivers", VendorSites.For(chipVendor ?? Wmi.Str(v, "AdapterCompatibility")));
                if (ids.Success) g.Add("ID PCI", $"{ids.Groups[1].Value}:{ids.Groups[2].Value} · subsistema {ids.Groups[4].Value}:{ids.Groups[3].Value}".Replace("SUBSISTEMA", "subsistema"));
                groups.Add((name, g));
            }
        });
        return Section("gpu", "Placa de vídeo", names.Count > 0 ? string.Join(" + ", names) : null, [.. groups]);
    }

    // ------------------------------------------------------------------ armazenamento

    private static SummarySection Storage(List<string> problems)
    {
        var groups = new List<(string?, Lines)>();
        double totalBytes = 0;
        Try(problems, "MSFT_PhysicalDisk", () =>
        {
            var pnpByNumber = new Dictionary<string, string>();
            foreach (var d in Wmi.Query(Wmi.Cimv2, "SELECT Index, PNPDeviceID FROM Win32_DiskDrive"))
                if (Wmi.Str(d, "PNPDeviceID") is { } id) pnpByNumber[Wmi.Get<uint?>(d, "Index")?.ToString(CultureInfo.InvariantCulture) ?? ""] = id;
            var styles = new Dictionary<string, string>();
            foreach (var d in Wmi.Query(Wmi.Storage, "SELECT Number, PartitionStyle, IsBoot FROM MSFT_Disk"))
            {
                var style = Wmi.Get<int?>(d, "PartitionStyle") switch { 1 => "MBR", 2 => "GPT", _ => "não inicializado" };
                styles[Wmi.Get<uint?>(d, "Number")?.ToString(CultureInfo.InvariantCulture) ?? ""] = style + (Wmi.Get<bool?>(d, "IsBoot") == true ? " · " + L.T("disco do sistema") : "");
            }

            var disks = Wmi.Query(Wmi.Storage, "SELECT DeviceId, FriendlyName, Model, SerialNumber, FirmwareVersion, BusType, MediaType, Size, SpindleSpeed, LogicalSectorSize, PhysicalSectorSize FROM MSFT_PhysicalDisk");
            foreach (var d in disks.OrderBy(d => int.TryParse(Wmi.Str(d, "DeviceId"), out var n) ? n : 99))
            {
                var number = Wmi.Str(d, "DeviceId") ?? "";
                var size = (double)(Wmi.Get<ulong?>(d, "Size") ?? 0);
                totalBytes += size;
                var bus = Wmi.Get<int?>(d, "BusType") switch
                {
                    17 => "NVMe", 11 => "SATA", 7 => "USB", 3 => "ATA", 10 => "SAS", 8 => "RAID", 1 => "SCSI", 12 => "SD", 16 => "Espaços de Armazenamento", var b => $"barramento {b}",
                };
                var media = Wmi.Get<int?>(d, "MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => null };
                var g = new Lines();
                g.Add("Tipo", $"{media} {bus}".Trim());
                g.Add("Capacidade", Gb(size));
                g.Add("Firmware", Wmi.Str(d, "FirmwareVersion"));
                g.Add("Número de série", Wmi.Str(d, "SerialNumber")?.TrimEnd('.'));
                if (Wmi.Get<uint?>(d, "SpindleSpeed") is { } rpm and > 0 and < uint.MaxValue) g.Add("Rotação", $"{rpm} RPM");
                var logical = Wmi.Get<ulong?>(d, "LogicalSectorSize");
                var physical = Wmi.Get<ulong?>(d, "PhysicalSectorSize");
                if (logical is > 0) g.Add("Setor", logical == physical ? $"{logical} bytes" : L.T("{0} bytes lógico · {1} físico", logical, physical));
                g.Add("Partições", styles.GetValueOrDefault(number));
                if (bus == "NVMe" && pnpByNumber.TryGetValue(number, out var pnp)) g.Add("Interface", PcieText(Native.GetPcieLink(pnp)));
                g.Add("Disco nº", number);
                g.Add("Site", VendorSites.For(Wmi.Str(d, "FriendlyName") ?? Wmi.Str(d, "Model")));
                groups.Add((Wmi.Str(d, "FriendlyName") ?? Wmi.Str(d, "Model") ?? $"Disco {number}", g));
            }
        });
        return Section("storage", "Armazenamento", groups.Count > 0 ? $"{groups.Count} {(groups.Count == 1 ? "disco" : "discos")} · {Gb(totalBytes)}" : null, [.. groups]);
    }

    // ------------------------------------------------------------------ rede

    private static SummarySection Network(List<string> problems)
    {
        var groups = new List<(string?, Lines)>();
        Try(problems, "MSFT_NetAdapter", () =>
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces().ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
            var adapters = Wmi.Query(Wmi.StandardCimv2, "SELECT Name, InterfaceDescription, InterfaceGuid, PermanentAddress, ReceiveLinkSpeed, DriverProvider, DriverVersionString, DriverDate, MediaConnectState, HardwareInterface, Virtual, NdisPhysicalMedium, ConnectorPresent, Hidden, PnPDeviceID FROM MSFT_NetAdapter")
                // Wi-Fi 7 (multi-link) cria várias interfaces da mesma placa ("Wi-Fi", "Wi-Fi 4", "Wi-Fi 5"): uma por placa.
                .GroupBy(a => Wmi.Str(a, "PnPDeviceID") ?? Wmi.Str(a, "InterfaceGuid") ?? "")
                .Select(g => g.OrderBy(a => Wmi.Get<uint?>(a, "MediaConnectState") == 1 ? 0 : 1).ThenBy(a => Wmi.Str(a, "Name")?.Length ?? 99).First())
                .OrderBy(a => Wmi.Get<uint?>(a, "MediaConnectState") == 1 ? 0 : 1).ThenBy(a => Wmi.Str(a, "Name"));
            foreach (var a in adapters)
            {
                // Fora: placas virtuais e as interfaces auxiliares do Wi-Fi (Wi-Fi Direct aparece como "Wi-Fi 4/5").
                if (Wmi.Get<bool?>(a, "Virtual") == true || Wmi.Get<bool?>(a, "HardwareInterface") == false
                    || Wmi.Get<bool?>(a, "ConnectorPresent") == false || Wmi.Get<bool?>(a, "Hidden") == true) continue;
                var g = new Lines();
                var connected = Wmi.Get<uint?>(a, "MediaConnectState") == 1;
                g.Add("Placa", Wmi.Str(a, "InterfaceDescription"));
                g.Add("Tipo", Wmi.Get<uint?>(a, "NdisPhysicalMedium") switch { 9 => "Wi-Fi", 10 => "Bluetooth", 14 => "Ethernet (802.3)", 0 => "Ethernet", _ => null });
                if (connected && Wmi.Get<ulong?>(a, "ReceiveLinkSpeed") is { } bps and > 0)
                    g.Add("Velocidade do link", bps >= 1_000_000_000 ? $"{(bps / 1e9).ToString("0.#", Pt)} Gbit/s" : $"{bps / 1_000_000} Mbit/s");
                g.Add("Estado", connected ? "Conectada" : "Desconectada");
                if (Wmi.Str(a, "PermanentAddress") is { Length: 12 } mac)
                    g.Add("MAC", string.Join(":", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2))));
                if (Wmi.Str(a, "InterfaceGuid") is { } guid && nics.TryGetValue(guid, out var nic) && connected)
                {
                    var ip = nic.GetIPProperties();
                    var v4 = ip.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(u => $"{u.Address}/{u.PrefixLength}");
                    g.Add("IPv4", string.Join(", ", v4));
                    g.Add("Gateway", string.Join(", ", ip.GatewayAddresses.Select(x => x.Address).Where(x => x.AddressFamily == AddressFamily.InterNetwork)));
                    g.Add("DNS", string.Join(", ", ip.DnsAddresses.Where(x => x.AddressFamily == AddressFamily.InterNetwork)));
                    try { g.Add("DHCP", YesNo(ip.GetIPv4Properties()?.IsDhcpEnabled)); }
                    catch (NetworkInformationException) { }
                }
                g.Add("Driver", $"{Wmi.Str(a, "DriverProvider")} {Wmi.Str(a, "DriverVersionString")}".Trim());
                g.Add("Site", VendorSites.For(Wmi.Str(a, "InterfaceDescription")));
                groups.Add((Wmi.Str(a, "Name"), g));
            }
        });
        var connectedCount = groups.Count(g => g.Item2.Any(i => i.Label == "Estado" && i.Value == "Conectada"));
        return Section("network", "Rede", groups.Count > 0 ? $"{groups.Count} {(groups.Count == 1 ? "placa" : "placas")} · {connectedCount} conectada{(connectedCount == 1 ? "" : "s")}" : null, [.. groups]);
    }

    // ------------------------------------------------------------------ monitores

    private static readonly Dictionary<string, string> MonitorVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DEL"] = "Dell", ["SAM"] = "Samsung", ["GSM"] = "LG", ["LGD"] = "LG Display", ["AUS"] = "ASUS", ["ACR"] = "Acer",
        ["BNQ"] = "BenQ", ["AOC"] = "AOC", ["MSI"] = "MSI", ["HWP"] = "HP", ["HPN"] = "HP", ["LEN"] = "Lenovo", ["PHL"] = "Philips",
        ["VSC"] = "ViewSonic", ["GBT"] = "Gigabyte", ["SNY"] = "Sony", ["ENC"] = "EIZO", ["AUO"] = "AU Optronics",
        ["BOE"] = "BOE", ["CMN"] = "Innolux", ["SHP"] = "Sharp", ["APP"] = "Apple", ["XMI"] = "Xiaomi", ["KTC"] = "KTC",
    };

    private static SummarySection Displays(List<string> problems)
    {
        var groups = new List<(string?, Lines)>();
        Try(problems, "WmiMonitorID", () =>
        {
            static string Text(ManagementBaseObject o, string p) => Wmi.Get<ushort[]>(o, p) is { } chars
                ? new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim() : "";
            Dictionary<string, ManagementObject> ByInstance(string cls) => Wmi.Query(@"root\wmi", $"SELECT * FROM {cls}")
                .GroupBy(o => Wmi.Str(o, "InstanceName") ?? "").ToDictionary(g => g.Key, g => g.First());
            var basic = ByInstance("WmiMonitorBasicDisplayParams");
            Dictionary<string, ManagementObject> modes = new(), conn = new();
            try { modes = ByInstance("WmiMonitorListedSupportedSourceModes"); } catch (ManagementException) { }
            try { conn = ByInstance("WmiMonitorConnectionParams"); } catch (ManagementException) { }

            foreach (var id in Wmi.Query(@"root\wmi", "SELECT * FROM WmiMonitorID"))
            {
                var instance = Wmi.Str(id, "InstanceName") ?? "";
                var vendorCode = Text(id, "ManufacturerName");
                var vendor = MonitorVendors.GetValueOrDefault(vendorCode, vendorCode);
                var model = Text(id, "UserFriendlyName");
                var g = new Lines();
                g.Add("Fabricante", vendor);
                if (modes.TryGetValue(instance, out var m) && Wmi.Get<ManagementBaseObject[]>(m, "MonitorSourceModes") is { Length: > 0 } list)
                {
                    // A lista do EDID básico para em ~75 Hz em monitores rápidos; só a resolução nativa é confiável aqui.
                    var idx = Wmi.Get<int?>(m, "PreferredMonitorSourceModeIndex") ?? 0;
                    var pref = list[Math.Clamp(idx, 0, list.Length - 1)];
                    g.Add("Resolução nativa", $"{Wmi.Get<int?>(pref, "HorizontalActivePixels")} × {Wmi.Get<int?>(pref, "VerticalActivePixels")}");
                }
                if (basic.TryGetValue(instance, out var b) && Wmi.Get<int?>(b, "MaxHorizontalImageSize") is { } w and > 0 && Wmi.Get<int?>(b, "MaxVerticalImageSize") is { } h and > 0)
                    g.Add("Tamanho", $"{(Math.Sqrt(w * w + h * h) / 2.54).ToString("0.#", Pt)}\" ({w} × {h} cm)");
                if (conn.TryGetValue(instance, out var c))
                    g.Add("Conexão", Wmi.Get<long?>(c, "VideoOutputTechnology") switch
                    {
                        0 => "VGA", 4 => "DVI", 5 => "HDMI", 10 => "DisplayPort", 11 => "DisplayPort (interno)", 2147483648 => "Tela interna", _ => null,
                    });
                if (Wmi.Get<int?>(id, "YearOfManufacture") is > 1990 and var year) g.Add("Fabricado em", $"{year}{(Wmi.Get<int?>(id, "WeekOfManufacture") is > 0 and <= 53 and var wk ? $", semana {wk}" : "")}");
                g.Add("Código", $"{vendorCode}{Text(id, "ProductCodeID")}");
                g.Add("Site", VendorSites.For(vendor, model));
                // "DELL UP2516D" → "Dell UP2516D"
                var shortModel = Regex.Replace(model, $@"^({Regex.Escape(vendor)}|{Regex.Escape(vendorCode)})\s+", "", RegexOptions.IgnoreCase);
                groups.Add((string.IsNullOrEmpty(model) ? $"Monitor {vendor}" : $"{vendor} {shortModel}".Trim(), g));
            }
        });
        return Section("display", "Monitores", groups.Count switch { 0 => "Nenhum monitor conectado", 1 => groups[0].Item1, var n => $"{n} monitores" }, [.. groups]);
    }

    // ------------------------------------------------------------------ áudio

    private static SummarySection Audio(List<string> problems)
    {
        var lines = new Lines();
        Try(problems, "Win32_SoundDevice", () =>
        {
            foreach (var name in Wmi.Query(Wmi.Cimv2, "SELECT Name, Manufacturer FROM Win32_SoundDevice")
                         .Select(s => (Name: Wmi.Str(s, "Name"), Maker: Wmi.Str(s, "Manufacturer")))
                         .Where(s => s.Name is not null).DistinctBy(s => s.Name).OrderBy(s => s.Name))
                lines.Add(name.Name!, name.Maker is null || name.Maker.StartsWith('(') ? "—" : name.Maker);
        });
        return Section("audio", "Áudio", lines.Count > 0 ? $"{lines.Count} dispositivo{(lines.Count == 1 ? "" : "s")}" : null, (null, lines));
    }

    // ------------------------------------------------------------------ energia

    private static readonly Dictionary<string, string> PowerPlans = new(StringComparer.OrdinalIgnoreCase)
    {
        ["381b4222-f694-41f0-9685-ff5bb260df2e"] = "Equilibrado",
        ["8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"] = "Alto desempenho",
        ["a1841308-3541-4fab-bc81-f71556f20b4a"] = "Economia de energia",
        ["e9a42b02-d5df-448d-aa00-03f14749eb61"] = "Desempenho máximo",
    };

    private static SummarySection Power(List<string> problems)
    {
        var lines = new Lines();
        var battery = new Lines();
        string? headline = null;
        Try(problems, "plano de energia", () =>
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");
            if (key?.GetValue("ActivePowerScheme") is not string guid) return;
            var name = PowerPlans.GetValueOrDefault(guid);
            if (name is null)
            {
                using var plan = key.OpenSubKey(guid);
                var friendly = plan?.GetValue("FriendlyName") as string;
                name = friendly is null || friendly.StartsWith('@') ? "Personalizado" : friendly;
            }
            headline = name;
            lines.Add("Plano ativo", name);
        });
        Try(problems, "Win32_Battery", () =>
        {
            var b = First(Wmi.Cimv2, "SELECT Name, EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
            if (b is null) return;
            var charge = Wmi.Get<int?>(b, "EstimatedChargeRemaining");
            battery.Add("Carga", charge is null ? null : $"{charge} %");
            battery.Add("Estado", Wmi.Get<int?>(b, "BatteryStatus") switch { 2 => "Na tomada", 1 => "Na bateria", 6 or 7 or 8 or 9 => "Carregando", 3 => "Carregada", _ => null });
            battery.Add("Modelo", Wmi.Str(b, "Name"));
            try
            {
                var design = First(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData");
                var full = First(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity");
                var d = design is null ? 0 : Wmi.Get<uint?>(design, "DesignedCapacity") ?? 0;
                var f = full is null ? 0 : Wmi.Get<uint?>(full, "FullChargedCapacity") ?? 0;
                if (d > 0 && f > 0) battery.Add("Saúde", L.T("{0:0} % ({1:0.#} de {2:0.#} Wh)", Math.Min(100, f * 100.0 / d), f / 1000.0, d / 1000.0));
            }
            catch (ManagementException)
            {
                // sem dados detalhados da bateria
            }
            headline = $"{headline} · bateria {charge} %";
        });
        return Section("power", "Energia", headline, (null, lines), ("Bateria", battery));
    }
}
