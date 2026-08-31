using Microsoft.VisualStudio.TestTools.UnitTesting;
using Pinboard.App.Compatibility;

namespace Pinboard.App.Tests;

[TestClass]
public sealed class LzStringCodecTests
{
    [DataTestMethod]
    [DataRow("", "Q===")]
    [DataRow("hello world", "BYUwNmD2AEDukCcwBMg=")]
    [DataRow("{\"type\":\"excalidraw\",\"version\":2}", "N4IgLgngDgpiBcIYA8DGBDANgSwCYCd0B3EAGhADcZ8BnbAewDsEAmAXyA==")]
    [DataRow("中文 OCR 😀 line\nSecond line", "rRynDTAEDyDCBKaLwbgAPbAGwJYDsCmAoAylgMYD2GAJqplkA===")]
    public void Base64Codec_MatchesCanonicalLzStringVectors(string source, string encoded)
    {
        Assert.AreEqual(encoded, LzStringCodec.CompressToBase64(source));
        Assert.AreEqual(source, LzStringCodec.DecompressFromBase64(encoded));
    }

    [TestMethod]
    public void DecompressFromBase64_RejectsCharactersOutsideAlphabet()
    {
        Assert.IsNull(LzStringCodec.DecompressFromBase64("not valid!"));
    }
}
