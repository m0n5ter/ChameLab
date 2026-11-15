namespace ChameLab.Cli;

internal static class HidLimits
{
    public static void Validate(string format, long fc, long cn, long il, long oem)
    {
        long[] limits = format switch
        {
            "H10301" => [255L, 65535L, 0L, 0L],
            "IND26" => [4095L, 4095L, 0L, 0L],
            "IND27" => [8191L, 16383L, 0L, 0L],
            "INDASC27" => [8191L, 16383L, 0L, 0L],
            "TECOM27" => [2047L, 65535L, 0L, 0L],
            "W2804" => [255L, 32767L, 0L, 0L],
            "IND29" => [8191L, 65535L, 0L, 0L],
            "ATSW30" => [4095L, 65535L, 0L, 0L],
            "ADT31" => [15L, 8388607L, 0L, 0L],
            "HCP32" => [0L, 16383L, 0L, 0L],
            "HPP32" => [4095L, 524287L, 0L, 0L],
            "KASTLE" => [255L, 65535L, 31L, 0L],
            "KANTECH" => [255L, 65535L, 0L, 0L],
            "WIE32" => [4095L, 65535L, 0L, 0L],
            "D10202" => [127L, 16777215L, 0L, 0L],
            "H10306" => [65535L, 65535L, 0L, 0L],
            "N10002" => [65535L, 65535L, 0L, 0L],
            "OPTUS34" => [1023L, 65535L, 0L, 0L],
            "SMP34" => [1023L, 65535L, 7L, 0L],
            "BQT34" => [255L, 16777215L, 0L, 0L],
            "C1K35S" => [4095L, 1048575L, 0L, 0L],
            "C15001" => [255L, 65535L, 0L, 1023L],
            "S12906" => [255L, 16777215L, 3L, 0L],
            "ACTPHID" => [255L, 16777215L, 0L, 1023L],
            "SIE36" => [262143L, 65535L, 0L, 0L],
            "H10320" => [0L, 99999999L, 0L, 0L],
            "H10302" => [0L, 34359738367L, 0L, 0L],
            "H10304" => [65535L, 524287L, 0L, 0L],
            "P10004" => [8191L, 262143L, 0L, 0L],
            "HGEN37" => [0L, 4294967295L, 0L, 0L],
            "MDI37" => [15L, 536870911L, 0L, 0L],
            _ => [uint.MaxValue, 0xFFFFFFFFFFL, byte.MaxValue, ushort.MaxValue]
        };
        var values = new[] { fc, cn, il, oem };
        var names = new[] { "facility code", "card number", "issue level", "OEM" };
        for (var i = 0; i < values.Length; i++)
            if (values[i] < 0 || values[i] > limits[i]) throw new ArgumentException($"{format}: {names[i]} must be between 0 and {limits[i]}.");
    }
}
