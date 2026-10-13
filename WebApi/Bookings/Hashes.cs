using System.Security.Cryptography;
using System.Text;

namespace WebApi.Bookings;

public static class Hashes
{
    public static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }
}
