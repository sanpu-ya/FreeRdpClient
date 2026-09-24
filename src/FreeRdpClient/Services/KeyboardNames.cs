using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FreeRdpClient.Services;

/// <summary>Kind of physical keyboard announced to the server.</summary>
public enum HardwareKeyboard
{
    /// <summary>Use the Windows setting (Settings &gt; Time &amp; language &gt; Language &gt; Options).</summary>
    Auto,
    Japanese106,
    English101,
}

/// <summary>Keyboard layout announced to the server.</summary>
public enum RemoteKeyboardLayout
{
    /// <summary>The current Windows input language.</summary>
    Auto,
    Japanese,
    /// <summary>US English. Useful for xrdp, which picks the X keymap from the layout only.</summary>
    English,
}

/// <summary>Human readable names for RDP keyboard layouts / types and the Windows hardware keyboard setting.</summary>
public static partial class KeyboardNames
{
    private const uint LangJapanese = 0x0411;
    private const string I8042Parameters = @"SYSTEM\CurrentControlSet\Services\i8042prt\Parameters";

    /// <summary>
    /// Localized layout name from HKLM\SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{KLID}
    /// (e.g. 0x00000411 = "日本語", 0x00000409 = "US"), or the hex id if unknown.
    /// </summary>
    public static string LayoutName(uint layoutId)
    {
        var klid = layoutId.ToString("X8");
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{klid}");
            if (key?.GetValue("Layout Display Name") is string indirect)
            {
                var buffer = new char[256];
                if (SHLoadIndirectString(indirect, buffer, buffer.Length, IntPtr.Zero) == 0)
                {
                    var length = Array.IndexOf(buffer, '\0');
                    if (length > 0)
                        return new string(buffer, 0, length);
                }
            }
            if (key?.GetValue("Layout Text") is string text && text.Length > 0)
                return text;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }

        return $"0x{klid}";
    }

    /// <summary>RDP keyboard type (TS_UD_CS_CORE keyboardType / keyboardSubType).</summary>
    public static string TypeName(uint type, uint subType) => type switch
    {
        1 => "IBM PC/XT (83 キー)",
        2 => "Olivetti (102 キー)",
        3 => "IBM PC/AT (84 キー)",
        4 => "英語キーボード (101/102 キー)",
        5 => "Nokia 1050",
        6 => "Nokia 9140",
        7 => subType == 0 ? "英語キーボード (101/102 キー)" : "日本語キーボード (106/109 キー)",
        8 => "韓国語キーボード",
        _ => $"タイプ {type}",
    };

    public static string Name(HardwareKeyboard keyboard) => keyboard switch
    {
        HardwareKeyboard.Japanese106 => "日本語キーボード (106/109 キー)",
        HardwareKeyboard.English101 => "英語キーボード (101/102 キー)",
        _ => $"自動 (Windows の設定: {Name(DetectWindowsSetting())})",
    };

    /// <summary>
    /// The "hardware keyboard layout" chosen in Windows Settings (Japanese language options).
    /// Windows stores it for the keyboard drivers in i8042prt\Parameters
    /// ("LayerDriver JPN" = kbd101.dll / kbd106.dll, OverrideKeyboardType/Subtype).
    /// </summary>
    public static HardwareKeyboard DetectWindowsSetting()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(I8042Parameters);
            if (key?.GetValue("OverrideKeyboardSubtype") is int subType &&
                key.GetValue("OverrideKeyboardType") is int type && type == 7)
            {
                return subType == 0 ? HardwareKeyboard.English101 : HardwareKeyboard.Japanese106;
            }

            if (key?.GetValue("LayerDriver JPN") is string driver && driver.Length > 0)
            {
                return driver.Equals("kbd101.dll", StringComparison.OrdinalIgnoreCase)
                    ? HardwareKeyboard.English101
                    : HardwareKeyboard.Japanese106;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }

        // Not configured: the keyboard driver's own report (7 = Japanese keyboard)
        return GetKeyboardType(0) == 7 && GetKeyboardType(1) != 0 ? HardwareKeyboard.Japanese106 : HardwareKeyboard.English101;
    }

    public static string Name(RemoteKeyboardLayout layout) => layout switch
    {
        RemoteKeyboardLayout.Japanese => "日本語",
        RemoteKeyboardLayout.English => "英語 (US) - xrdp (Linux) で記号がずれる場合",
        _ => $"自動 (Windows の入力言語: {LayoutName((uint)GetKeyboardLayout(0) & 0xFFFF)})",
    };

    /// <summary>Layout id to announce, or null to let the client detect the input language.</summary>
    public static uint? ToRdpLayoutId(RemoteKeyboardLayout layout) => layout switch
    {
        RemoteKeyboardLayout.Japanese => 0x00000411,
        RemoteKeyboardLayout.English => 0x00000409,
        _ => null,
    };

    /// <summary>
    /// Keyboard type/subtype/function keys to announce, or null to leave FreeRDP's default
    /// (non Japanese layouts with the automatic setting).
    /// </summary>
    public static (uint Type, uint SubType, uint FunctionKeys)? ToRdpKeyboardType(HardwareKeyboard keyboard,
        RemoteKeyboardLayout layout = RemoteKeyboardLayout.Auto)
    {
        var japaneseLayout = layout switch
        {
            RemoteKeyboardLayout.Japanese => true,
            RemoteKeyboardLayout.English => false,
            _ => ((uint)GetKeyboardLayout(0) & 0xFFFF) == LangJapanese,
        };
        if (keyboard == HardwareKeyboard.Auto)
        {
            if (!japaneseLayout)
                return null;
            keyboard = DetectWindowsSetting();
        }

        return keyboard switch
        {
            HardwareKeyboard.Japanese106 => (7u, 2u, 12u),
            // Japanese layout on an English keyboard is type 7 / sub type 0 (kbd101.dll)
            _ => japaneseLayout ? (7u, 0u, 12u) : (4u, 0u, 12u),
        };
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHLoadIndirectString(string source, [Out] char[] buffer, int size, IntPtr reserved);

    [LibraryImport("user32.dll")]
    private static partial int GetKeyboardType(int typeFlag);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetKeyboardLayout(uint threadId);
}
