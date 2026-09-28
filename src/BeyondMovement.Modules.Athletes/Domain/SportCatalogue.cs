namespace BeyondMovement.Modules.Athletes.Domain;

/// <summary>
/// The sports the application ships with. <b>This list is the catalogue</b> — it is seeded into
/// the <c>Sports</c> table through the model, so adding a sport is: add a line here, generate a
/// migration, deploy. There is deliberately no Admin endpoint for it.
/// <para>
/// Every id is fixed here rather than generated at startup, so the same sport has the same id in
/// every environment and a client, a test or a migration can name one. <b>Never change or reuse
/// an id</b> once shipped — athlete profiles point at it. Renaming a sport is safe (a profile
/// stores the id, never the name); removing one that anybody has picked is not, and the foreign
/// key refuses it.
/// </para>
/// </summary>
public static class SportCatalogue
{
    /// <summary>The ordering group every ordinary sport is in, so they read alphabetically.</summary>
    public const int Ordinary = 0;

    /// <summary>After every ordinary sport, whatever its name.</summary>
    public const int Last = 1;

    public static readonly Guid ArtisticSwimming = new("0b6f3c1e-58a2-4d7e-9c41-2f8a6e1d3b70");
    public static readonly Guid Football = new("5e2c7a94-1f3b-4c86-a0d5-7b9e2c4f1a38");
    public static readonly Guid Tennis = new("c4a81e27-9d5f-4b3a-8e6c-1a7d9f2b5e04");
    public static readonly Guid Other = new("f0e9d8c7-2b4a-4f61-9d3e-8c5b7a1e6d29");

    public static IReadOnlyList<Sport> All { get; } =
    [
        new(ArtisticSwimming, "Artistic Swimming", Ordinary),
        new(new Guid("3d9a5f12-7c4e-4a8b-b1f6-5e2d8c9a0f47"), "Athletics", Ordinary),
        new(new Guid("8a1c4e7b-2d9f-4e35-a6b8-0f3c7d1e9a52"), "Badminton", Ordinary),
        new(new Guid("61f7b3d8-4a2c-4e9d-8b5a-3c6e0f9d2a17"), "Basketball", Ordinary),
        new(new Guid("2e8d6a41-9b3f-4c7e-a5d2-6f1b8c4e3a90"), "Boxing", Ordinary),
        new(new Guid("9c3f1a6e-5d8b-4b2a-9e7c-4a0d6f2b8e13"), "Cycling", Ordinary),
        new(new Guid("47b2e9d5-3a6c-4f1e-8d4b-9e5a2c7f0b61"), "Diving", Ordinary),
        new(new Guid("d5a8c2f7-6e1b-4d93-b7a4-2c9f5e8d1a36"), "Equestrian", Ordinary),
        new(new Guid("1f4e7b9a-8c2d-4a56-9f3e-7b1d4a6c9e82"), "Fencing", Ordinary),
        new(new Guid("b8d1f5a3-4e7c-4c29-a8f1-5d3b9e6a2c74"), "Fin Swimming", Ordinary),
        new(Football, "Football", Ordinary),
        new(new Guid("6a9e3d1c-7f5b-4e8a-b2c6-8d4f1a3e7b95"), "Golf", Ordinary),
        new(new Guid("e2c5a8f1-3b9d-4f7a-8c1e-4b7d2f9a5c63"), "Gymnastics", Ordinary),
        new(new Guid("73d1b6e9-2a4f-4c8b-9e5d-1f6a3c8b4d27"), "Handball", Ordinary),
        new(new Guid("a4f8c3e6-9d2b-4a71-b5e8-3c1f7d9a6e48"), "Judo", Ordinary),
        new(new Guid("0c7e2a9d-5f3b-4d86-a1c4-9e2b5f8d3a16"), "Karate", Ordinary),
        new(new Guid("58b3f1d7-6c9a-4e2f-8a7d-2e5c9b1f4d83"), "Modern Pentathlon", Ordinary),
        new(new Guid("c9e6a2d4-1b8f-4f53-9d2a-6b4e8c3f1a57"), "Padel", Ordinary),
        new(new Guid("24a7d9f3-8e1c-4b6a-a3f9-5c8e2d6b9f10"), "Rowing", Ordinary),
        new(new Guid("f7b4e1c8-3d6a-4a2e-b9c5-1d7f4a8e2c36"), "Rugby", Ordinary),
        new(new Guid("3b8f6d2a-9c4e-4e71-8f3b-7a2d5e9c6b84"), "Sailing", Ordinary),
        new(new Guid("8e2a5c9f-4d7b-4c38-a6e1-9f3b7d2a5c49"), "Shooting", Ordinary),
        new(new Guid("5d9c1f4b-2e8a-4b67-9c3d-4e8a1f6b9d25"), "Squash", Ordinary),
        new(new Guid("b1e7a3d6-9f2c-4d85-8b4e-6c2f9a3d7e18"), "Swimming", Ordinary),
        new(new Guid("46c2f8b9-1a5d-4f3e-a7c2-8d5b3e9f1a64"), "Table Tennis", Ordinary),
        new(new Guid("da3f9b5e-6c1a-4a28-b3d7-2f9e5c1a8b37"), "Taekwondo", Ordinary),
        new(Tennis, "Tennis", Ordinary),
        new(new Guid("7f1d4a8c-3e6b-4e92-9a5f-1b4d7c2e8f56"), "Trampoline", Ordinary),
        new(new Guid("2a6e9c3f-8b1d-4c74-8e2a-5f9c3b6d1e42"), "Triathlon", Ordinary),
        new(new Guid("9b4c7e1a-5f2d-4b39-a8c6-3e7a1d5f9b28"), "Volleyball", Ordinary),
        new(new Guid("e5d2b8f4-7a3c-4f16-9b1e-8c4a2f7d3b69"), "Water Polo", Ordinary),
        new(new Guid("13f8a6c2-4d9e-4a5b-b7f3-9d1c6e4a8f75"), "Weightlifting", Ordinary),
        new(new Guid("c6a3e9d1-2b7f-4d48-8a5c-7e3f1b9d4a26"), "Wrestling", Ordinary),

        // Not a free-text escape hatch — there is no field to say which sport. It lets an athlete
        // whose sport is not listed yet finish onboarding; once it is added here, they re-pick.
        new(Other, "Other", Last)
    ];
}
