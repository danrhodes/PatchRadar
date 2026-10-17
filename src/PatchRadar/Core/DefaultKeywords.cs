namespace PatchRadar.Core;

public static class DefaultKeywords
{
    public static readonly string[] Critical =
    {
        "RADIUS", "802.1X", "802.11", "EAP", "PEAP", "MS-CHAP", "MS-CHAPv2", "NTLM", "NTLMv1", "Kerberos", "NPS",
        "Wi-Fi", "WPA", "VPN", "L2TP", "IKEv2", "SSTP", "Always On VPN", "Remote Desktop", "RDP",
        "BitLocker", "recovery key", "Secure Boot", "TPM", "Credential Guard", "LSASS", "Netlogon",
        "Active Directory", "domain controller", "DNS", "DHCP", "SMB", "certificate", "authentication",
        "sign-in", "logon", "Windows Hello", "Hyper-V", "cluster", "print", "spooler",
        "boot", "BSOD", "blue screen", "stop error", "black screen", "crash", "data loss", "fails to start",
    };

    public static readonly string[] ChangePhrases =
    {
        "changing the default", "default behavior", "enforce mode", "now blocks", "will no longer",
        "no longer supported", "deprecat", "strengthens", "hardening", "disabled by default",
        "enabled by default", "now requires", "audit mode",
    };
}
