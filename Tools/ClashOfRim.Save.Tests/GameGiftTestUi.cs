// Shared observations of the gift editor; transaction outcomes belong to each test script.
internal static class GameGiftTestUi
{
    internal static object? Reply(string method, string label, int cargo) => method switch
    {
        "caravan.state" => new { items = new[] { new { defName = "WoodLog", count = cargo } } },
        "world.gizmos" => new { items = new[] { new { label = "Gift", type = "wrong", disabled = false }, new { label, type = "disabled", disabled = true }, new { label, type = "gift-command", disabled = false } } },
        "clash.labels" => new { gift = label },
        "menus.list" => new { items = new[] { Ref("target-menu") } },
        "menus.options" => new { items = new[] { new { reference = new { handle = "recipient" }, disabled = false } } },
        "ui.windows" => new { items = new[] { Ref("other-window", "Other"), Ref("gift-window", "Mod.GiftDeliveryDialogWindow") } },
        "clash.gift.inventory" => new { items = new[] { new { reference = new { handle = "small" }, defName = "WoodLog", count = 1 }, new { reference = new { handle = "wood" }, defName = "WoodLog", count = 10 } } },
        _ => null
    };
    private static object Ref(string handle, string type = "test") => new { reference = new { handle, type } };
}
