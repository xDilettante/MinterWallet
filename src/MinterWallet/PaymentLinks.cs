using Minter.Protocol;

namespace MinterWallet;

// External links are untrusted payment requests. They can only prefill a Send;
// unlocking, network checks, commission estimation and signing stay unchanged.
public static class PaymentLinks
{
    public static MinterLink? Parse(string text)
    {
        if (text.Length > 1024 || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != "minterwallet" || uri.Host != "pay" || uri.UserInfo.Length != 0
            || !uri.IsDefaultPort || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !uri.AbsolutePath.StartsWith("/tx/", StringComparison.Ordinal)) return null;
        try
        {
            var link = MinterLink.Parse(text);
            return link is { Type: TxType.Send, Payload.Length: 0 } && link.DecodeData() is SendData { Value.Sign: > 0 } ? link : null;
        }
        catch (FormatException) { return null; }
    }
}
