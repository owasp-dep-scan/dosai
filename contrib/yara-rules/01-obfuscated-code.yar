rule DotNet_Obfuscated_Code
{
    meta:
        description = "Detects signs of code obfuscation in .NET assemblies"
        author = "Team AppThreat"
        reference = "Dosai JSON Analysis"
        date = "2025-10-18"
        
    strings:
        $obfuscated_class = /"ClassName":\s*"[a-zA-Z]{1,2}[0-9]{1,4}[a-zA-Z]{0,2}"/
        $obfuscated_method = /"Name":\s*"[a-zA-Z]{1,2}[0-9]{1,4}[a-zA-Z]{0,2}"/
        $base64_strings = /"ReturnType":\s*"System\.String"/
        $encoded_chars = /"Name":\s*"[\\x00-\\x1F\\x7F-\\x9F]"/
        $suspicious_namespace = /"Namespace":\s*"[a-zA-Z]{1,3}\.[a-zA-Z]{1,3}\.[a-zA-Z]{1,3}"/
        
    condition:
        2 of them
}
