using System.Drawing.Imaging;

namespace NexBot.App;

/// <summary>Saves a picture of every page (the hidden "--screenshots" argument; used to make the guide).</summary>
public partial class MainForm
{
    internal async Task CaptureAllPagesAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        _noSave = true;
        string[] names = ["server", "map", "character", "attack", "training", "protection", "town", "inventory", "pet", "pickfilter", "script", "autotrade", "party", "guild", "academy", "players", "stall", "alchemy", "conditions", "sound", "log", "gm"];
        sideNav.SetShown(pageGm, true); // the guide shows the GM page too
        pageInventory.DemoPortrait = @"interface\character\char_ch_man1.ddj"; // the guide shows a portrait
        pageCharacter.DemoPortrait = pageInventory.DemoPortrait;
        pageCharacter.SetSheet(null);
        pageInventory.SetSheet(null);
        const string face = @"interface\character\char_ch_";
        pageParty.DemoRows =
        [
            new(face + "man1.ddj", "Leader", "Lv. 98  ·  Phoenix", 100, 90, true),
            new(face + "woman3.ddj", "Healer", "Lv. 96  ·  Phoenix", 70, 40),
            new(face + "man5.ddj", "Archer", "Lv. 95", 30, 80),
        ];
        pageGuild.DemoRows =
        [
            new(face + "man1.ddj", "Leader", "Master  ·  Lv. 110  ·  online", Master: true),
            new(face + "woman2.ddj", "Healer", "Member  ·  Lv. 104  ·  online"),
            new(face + "man3.ddj", "Archer", "Member  ·  Lv. 99  ·  offline"),
        ];
        pagePet.DemoRows =
        [
            new(null, "Wolfie", "Attack pet  ·  Lv. 42  ·  HP 3,120  ·  HGP 81%", 90, 81),
            new(null, "Grab rabbit", "Grab pet  ·  3 items"),
        ];
        pagePlayers.DemoRows =
        [
            new(face + "man2.ddj", "Wanderer  [Phoenix]", "~Lv. 97  ·  4 m"),
            new(face + "woman1.ddj", "Merchant", "~Lv. 60  ·  Trader  ·  stall  ·  12 m"),
            new(face + "man4.ddj", "Hunter", "~Lv. 88  ·  Hunter  ·  25 m"),
        ];
        await Task.Delay(2500); // let the server config load
        var pages = Pages;
        for (int i = 0; i < pages.Length; i++)
        {
            sideNav.SelectPage(pages[i]);
            await Task.Delay(700);
            Save(names[i]);
            if (pages[i] == pageAttack)
            {
                string[] tabs = ["attack", "attack-buffs", "attack-partybuffs", "attack-resurrect", "attack-healing", "attack-lure", "attack-log"];
                for (int t = 1; t < tabs.Length; t++)
                {
                    pageAttack.SelectTab(t);
                    await Task.Delay(400);
                    Save(tabs[t]);
                }
                pageAttack.SelectTab(0);
            }
            if (pages[i] == pageTraining)
            {
                string[] tabs = ["training", "training-conditions", "training-collision", "training-settings", "training-script"];
                for (int t = 1; t < tabs.Length; t++)
                {
                    pageTraining.SelectTab(t);
                    await Task.Delay(400);
                    Save(tabs[t]);
                }
                pageTraining.SelectTab(0);
            }
            if (pages[i] == pageProtection)
            {
                string[] tabs = ["protection", "protection-return", "protection-dead", "protection-more"];
                for (int t = 1; t < tabs.Length; t++)
                {
                    pageProtection.SelectTab(t);
                    await Task.Delay(400);
                    Save(tabs[t]);
                }
                pageProtection.SelectTab(0);
            }
        }

        void Save(string name)
        {
            Refresh();
            using var bitmap = new Bitmap(Width, Height);
            DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
            // Only the client area (without the Windows title bar and borders).
            var client = RectangleToScreen(ClientRectangle);
            var crop = new Rectangle(client.X - Left, client.Y - Top, client.Width, client.Height);
            using var inner = bitmap.Clone(crop, bitmap.PixelFormat);
            inner.Save(Path.Combine(folder, name + ".png"), ImageFormat.Png);
        }
        Close();
    }

    /// <summary>True while taking screenshots: settings are not saved on close.</summary>
    private bool _noSave;
}
