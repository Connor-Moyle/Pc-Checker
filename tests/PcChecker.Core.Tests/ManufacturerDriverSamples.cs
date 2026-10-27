namespace PcChecker.Core.Tests;

/// <summary>
/// Saved maker answers, trimmed to the parts the parsers read (signatures, CRCs and page chrome removed): AMD's AM5
/// chipset page as served in October 2026, and Lenovo's ThinkPad T14 Gen 3 (21AH) catalog with its BIOS and Intel
/// AX211 Wi-Fi package descriptions.
/// </summary>
internal static class ManufacturerDriverSamples
{
    public const string AmdChipsetPage = """
        <section class="container" id="amd_support_driver_list" aria-labelledby="drivers-heading"> <div class="row"> <div class="col-12" id="main-content">
        <h2 id="drivers-heading">Drivers and Software</h2>
        <div class="accordion-item" id="accordion-content-component-6ba1c4d41d-category-0"> <h2 class="accordion-header">
        <button class="accordion-button collapsed" type="button"> Windows 11 - 64-Bit Edition </button> </h2>
        <div class="accordion-body"> <div class="category"> <h3 class="category-title"> Driver </h3>
        <article class="container-fluid driver-download-details" style="background-color: rgb(241, 241, 242);"> <div class="row">
          <div class="col-12"> <h4>AMD Chipset Drivers</h4> </div>
          <div class="col-6 col-lg"> <strong>Revision Number</strong> <p>8.09.28.1124</p> </div>
          <div class="col-6 col-lg"> <strong>File Size</strong> <p>79 MB</p> </div>
          <div class="col-6 col-lg"> <strong>Release Date</strong> <p> 2026-10-21 </p> </div>
          <div class="col-6 col-lg"> <div class="button btn btn-Primary-Black">
            <a href="https://drivers.amd.com/drivers/amd_chipset_software_8.09.28.1124.exe" aria-label="Download AMD Chipset Drivers" role="button">
            <span> Download<sup>*</sup> <span class="visually-hidden">AMD Chipset Drivers</span> </span> </a> </div> </div>
        </div> </article>
        <article class="container-fluid driver-download-details" style="background-color: rgb(241, 241, 242);"> <div class="row">
          <div class="col-12"> <h4>AMD RAID Installer (SATA, NVMe RAID)</h4> </div>
          <div class="col-6 col-lg"> <strong>Revision Number</strong> <p>8.03.24.733</p> </div>
          <div class="col-6 col-lg"> <strong>Release Date</strong> <p> 2026-04-16 </p> </div>
          <div class="col-6 col-lg"> <a href="https://drivers.amd.com/drivers/amd_raid_software_8.03.24.733.zip" role="button">Download</a> </div>
        </div> </article>
        </div> </div> </div> </div> </div> </section>
        """;

    /// <summary>The same page after a redesign that dropped the direct link: only the text is left.</summary>
    public const string AmdChipsetPageWithoutLink = """
        <article class="container-fluid driver-download-details"> <div class="row">
          <div class="col-12"> <h4>AMD Chipset Drivers</h4> </div>
          <div class="col-6 col-lg"> <strong>Revision Number</strong> <p>7.01.08.129</p> </div>
          <div class="col-6 col-lg"> <strong>File Size</strong> <p>71 MB</p> </div>
          <div class="col-6 col-lg"> <strong>Release Date</strong> <p> 1/31/2025 </p> </div>
          <div class="col-6 col-lg"> <a href="/en/support/download/chipset-download.html" role="button">Download</a> </div>
        </div> </article>
        """;

    public const string LenovoCatalogXml = """
        <?xml version="1.0" encoding="utf-8"?><packages count="5">
          <package>
            <location>https://download.lenovo.com/pccbbs/mobiles/tbt3wsdkfw3182_2_2_.xml</location>
            <category>Product Firmware</category>
            <oslangs></oslangs>
            <checksum type="sha256">f997523cba14978f3a9ec96dfefe89a40596f3401572792a080ac1876af2eb37</checksum>
          </package>
          <package>
            <location>https://download.lenovo.com/pccbbs/mobiles/n3ciw20w_2_.xml</location>
            <category>Networking Wireless LAN</category>
            <oslangs></oslangs>
          </package>
          <package>
            <location>https://download.lenovo.com/pccbbs/mobiles/n3bao30w_2_.xml</location>
            <category>Audio</category>
          </package>
          <package>
            <location>http://download.lenovo.com/pccbbs/mobiles/insecure.xml</location>
            <category>Audio</category>
          </package>
          <package>
            <location>https://download.lenovo.com/pccbbs/mobiles/n3buj24w_2_.xml</location>
            <category>BIOS UEFI</category>
          </package>
        </packages>
        """;

    public const string LenovoBiosPackage = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package name="BIOS_N3BUJ" id="n3buj24w" version="1.48/1.28" hide="False" pcdDescriptor="0230">
          <Title default="EN">
            <Desc id="EN">BIOS Update Utility - 10/11</Desc>
          </Title>
          <DetectVersion type="File" show="False">
            <File>%WINDOWS%\explorer.exe</File>
          </DetectVersion>
          <Vendor>Lenovo</Vendor>
          <ReleaseDate>2026-07-06</ReleaseDate>
          <Reboot type="5" />
          <PackageType type="3" />
          <DetectInstall>
            <Or>
              <_Bios><Level>N3BET70*</Level></_Bios>
              <_Bios><Level>N3MET29*</Level></_Bios>
            </Or>
          </DetectInstall>
          <Dependencies>
            <And>
              <Or>
                <_Bios>
                  <Level>N3BET28*</Level>
                  <Level>N3BET6*</Level>
                </_Bios>
                <_Bios>
                  <Level>N3MET28*</Level>
                </_Bios>
              </Or>
            </And>
          </Dependencies>
          <Files>
            <Installer><File><Name>n3buj24w.exe</Name><Size>22561432</Size></File></Installer>
            <Readme default="EN"><File id="EN"><Name>n3buj24w.html</Name><Size>173797</Size></File></Readme>
          </Files>
        </Package>
        """;

    public const string LenovoWlanPackage = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package name="WLAN_N3CIW" id="n3ciw20w" version="24.30.1.1" hide="False" pcdDescriptor="0230">
          <Title default="EN">
            <Desc id="EN">Intel AX211 WLAN Driver - 10 (20H2 or Later)/11</Desc>
          </Title>
          <DetectVersion type="Inf">
            <_PnPID><![CDATA[PCI\VEN_8086&DEV_51F0&SUBSYS_00908086]]></_PnPID>
          </DetectVersion>
          <Vendor>Lenovo</Vendor>
          <ReleaseDate>2026-05-19</ReleaseDate>
          <PackageType type="2" />
          <DetectInstall>
            <_Driver>
              <HardwareID><![CDATA[PCI\VEN_8086&DEV_51F0&SUBSYS_00908086]]></HardwareID>
              <HardwareID><![CDATA[PCI\VEN_8086&DEV_7AF0&SUBSYS_00908086]]></HardwareID>
              <Date>2026-03-03</Date>
              <Version>24.30.1.1^</Version>
            </_Driver>
          </DetectInstall>
          <Dependencies>
            <And>
              <_Bios><Level>*</Level><Level>N3BET*</Level></_Bios>
            </And>
          </Dependencies>
          <Files>
            <Installer><File><Name>n3ciw20w.exe</Name></File></Installer>
            <Readme default="EN"><File id="EN"><Name>n3ciw20w.html</Name></File></Readme>
          </Files>
        </Package>
        """;
}
