namespace AIRsLight.ClashOfRim.Events;

public sealed record EventLetterAction(
    EventLetterActionKind Kind,
    string Label,
    bool RequiresServerRoundtrip,
    bool ChangesLedgerState);
