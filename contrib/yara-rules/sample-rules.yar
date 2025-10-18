rule DotNet_Suspicious_API_Usage
{
    meta:
        description = "Detects usage of suspicious APIs in .NET assemblies"
        author = "Team AppThreat"
        reference = "Dosai JSON Analysis"
        date = "2025-10-18"
        
    strings:
        $process_start = /"CalledMethod":\s*"System\.Diagnostics\.Process\.Start"/
        $process_create = /"CalledMethod":\s*"System\.Diagnostics\.Process\.Create"/
        $file_write = /"CalledMethod":\s*"System\.IO\.File\.WriteAllText"/
        $file_read = /"CalledMethod":\s*"System\.IO\.File\.ReadAllText"/
        $file_delete = /"CalledMethod":\s*"System\.IO\.File\.Delete"/
        $registry_key = /"CalledMethod":\s*"Microsoft\.Win32\.Registry\.Key"/
        $web_request = /"CalledMethod":\s*"System\.Net\.WebRequest\.Create"/
        $socket_connect = /"CalledMethod":\s*"System\.Net\.Sockets\.Socket\.Connect"/
        $crypto_encrypt = /"CalledMethod":\s*"System\.Security\.Cryptography\.SymmetricAlgorithm\.CreateEncryptor"/
        $crypto_decrypt = /"CalledMethod":\s*"System\.Security\.Cryptography\.SymmetricAlgorithm\.CreateDecryptor"/
        $reflection_load = /"CalledMethod":\s*"System\.Reflection\.Assembly\.LoadFrom"/
        $reflection_create = /"CalledMethod":\s*"System\.Activator\.CreateInstance"/
        
    condition:
        any of them
}

rule DotNet_Anti_Analysis_Techniques
{
    meta:
        description = "Detects anti-analysis techniques in .NET assemblies"
        author = "Team AppThreat"
        reference = "Dosai JSON Analysis"
        date = "2025-10-18"
        
    strings:
        $debugger_check = /"CalledMethod":\s*"System\.Diagnostics\.Debugger\.IsAttached"/
        $debugger_break = /"CalledMethod":\s*"System\.Diagnostics\.Debugger\.Break"/
        $vm_check = /"CalledMethod":\s*"System\.Management\.ManagementObjectSearcher"/
        $timing_check = /"CalledMethod":\s*"System\.Diagnostics\.Stopwatch\.GetTimestamp"/
        $sandbox_check = /"CalledMethod":\s*"System\.Windows\.Forms\.Screen\.AllScreens"/
        
    condition:
        any of them
}

rule DotNet_Persistence_Mechanisms
{
    meta:
        description = "Detects persistence mechanisms in .NET assemblies"
        author = "Team AppThreat"
        reference = "Dosai JSON Analysis"
        date = "2025-10-18"
        
    strings:
        $registry_run = /"CalledMethod":\s*"Microsoft\.Win32\.Registry\.CurrentUser\.OpenSubKey".{0,500}"Software\\Microsoft\\Windows\\CurrentVersion\\Run"/
        $registry_runonce = /"CalledMethod":\s*"Microsoft\.Win32\.Registry\.CurrentUser\.OpenSubKey".{0,500}"Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce"/
        $task_service = /"CalledMethod":\s*"Microsoft\.Win32\.TaskScheduler\.TaskService"/
        $startup_path = /"CalledMethod":\s*"System\.Environment\.GetFolderPath".{0,500}"Startup"/
        $wmi_subscription = /"CalledMethod":\s*"System\.Management\.ManagementEventWatcher"/
        
    condition:
        any of them
}

rule DotNet_Data_Exfiltration_Patterns
{
    meta:
        description = "Detects potential data exfiltration patterns in .NET assemblies"
        author = "Team AppThreat"
        reference = "Dosai JSON Analysis"
        date = "2025-10-18"
        
    strings:
        $http_post = /"CalledMethod":\s*"System\.Net\.HttpWebRequest\.GetRequestStream"/
        $ftp_upload = /"CalledMethod":\s*"System\.Net\.FtpWebRequest\.GetRequestStream"/
        $smtp_send = /"CalledMethod":\s*"System\.Net\.Mail\.SmtpClient\.Send"/
        $clipboard_get = /"CalledMethod":\s*"System\.Windows\.Forms\.Clipboard\.GetData"/
        $screenshot = /"CalledMethod":\s*"System\.Windows\.Forms\.Screen\.CaptureScreen"/
        $keylogger = /"CalledMethod":\s*"System\.Windows\.Forms\.Control\.KeyPress"/
        $zip_create = /"CalledMethod":\s*"System\.IO\.Compression\.ZipFile\.CreateFromDirectory"/
        
    condition:
        2 of them
}

rule DotNet_Suspicious_Assembly_Metadata
{
    meta:
        description = "Detects suspicious assembly metadata"
        author = "Team AppThreat"
        reference = "Dosai JSON Analysis"
        date = "2025-10-18"
        
    strings:
        $minimal_metadata = /"AssemblyInformation":\s*\[\s*\{\s*"Name":\s*"[^"]+",\s*"Version":\s*"[^"]+"\s*\}\s*\]/
        $suspicious_company = /"Company":\s*"(Microsoft|Google|Apple|Adobe|Oracle|VMware)"/
        $suspicious_name = /"Name":\s*"(svchost|explorer|winlogon|csrss|lsass|services|spoolsv)"/
        $strong_name_bypass = /"CalledMethod":\s*"System\.Reflection\.Assembly\.LoadFrom".{0,500}"skipVerification"/
        
    condition:
        any of them
}

rule DotNet_Call_Graph_Anomaly
{
    meta:
        description = "Detects anomalies in method call graphs"
        author = "Team AppThreat"
        reference = "Dosai JSON Analysis"
        date = "2025-10-18"
        
    strings:
        $highly_connected = /"Id":\s*"[^"]+",\s*"Name":\s*"[^"]+",\s*"Edges":\s*\[\s*\{[^}]+\},\s*\{[^}]+\},\s*\{[^}]+\},\s*\{[^}]+\}/
        $recursive_call = /"SourceId":\s*"[^"]+",\s*"TargetId":\s*"[^"]+",\s*"SourceId":\s*"[^"]+"/
        $unusual_pattern = /"SourceId":\s*"[^"]*System\.[^"]*",\s*"TargetId":\s*"[^"]*Microsoft\.[^"]*"/
        
    condition:
        any of them
}

rule DotNet_Suspicious_Dependencies
{
    meta:
        description = "Detects suspicious dependencies in .NET assemblies"
        author = "Team AppThreat"
        reference = "Dosai JSON Analysis"
        date = "2025-10-18"
        
    strings:
        $malicious_lib = /"Name":\s*"(Reactor|ConfuserEx|Dotfuscator|SmartAssembly|Eazfuscator)"/
        $unusual_namespace = /"Namespace":\s*"(Microsoft\.Office\.Interop|System\.Management\.Automation|Microsoft\.Win32\.TaskScheduler)"/
        $network_lib = /"Name":\s*"(Newtonsoft\.Json|RestSharp|HttpClient|WebSocketSharp)"/
        $crypto_lib = /"Name":\s*"(BouncyCastle|CryptoPP|OpenSSL\.NET)"/
        
    condition:
        any of them
}