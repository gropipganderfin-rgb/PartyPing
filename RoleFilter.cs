using Dalamud.Game.Gui.PartyFinder.Types;

namespace PartyPing;

public enum RoleFilter
{
    AnyRole = 0,
    Tank = 1,
    Healer = 2,
    Melee = 3,
    PhysicalRanged = 4,
    Caster = 5,
    AnyDps = 6,
}

internal static class RoleFilterExtensions
{
    private static readonly HashSet<JobFlags> TankJobs =
    [
        JobFlags.Gladiator,
        JobFlags.Marauder,
        JobFlags.Paladin,
        JobFlags.Warrior,
        JobFlags.DarkKnight,
        JobFlags.Gunbreaker,
    ];

    private static readonly HashSet<JobFlags> HealerJobs =
    [
        JobFlags.Conjurer,
        JobFlags.WhiteMage,
        JobFlags.Scholar,
        JobFlags.Astrologian,
        JobFlags.Sage,
    ];

    private static readonly HashSet<JobFlags> MeleeJobs =
    [
        JobFlags.Pugilist,
        JobFlags.Lancer,
        JobFlags.Rogue,
        JobFlags.Monk,
        JobFlags.Dragoon,
        JobFlags.Ninja,
        JobFlags.Samurai,
        JobFlags.Reaper,
        JobFlags.Viper,
    ];

    private static readonly HashSet<JobFlags> PhysicalRangedJobs =
    [
        JobFlags.Archer,
        JobFlags.Bard,
        JobFlags.Machinist,
        JobFlags.Dancer,
    ];

    private static readonly HashSet<JobFlags> CasterJobs =
    [
        JobFlags.Thaumaturge,
        JobFlags.Arcanist,
        JobFlags.BlackMage,
        JobFlags.Summoner,
        JobFlags.RedMage,
        JobFlags.BlueMage,
        JobFlags.Pictomancer,
    ];

    public static bool Matches(this RoleFilter role, JobFlags job) => role switch
    {
        RoleFilter.AnyRole => true,
        RoleFilter.Tank => TankJobs.Contains(job),
        RoleFilter.Healer => HealerJobs.Contains(job),
        RoleFilter.Melee => MeleeJobs.Contains(job),
        RoleFilter.PhysicalRanged => PhysicalRangedJobs.Contains(job),
        RoleFilter.Caster => CasterJobs.Contains(job),
        RoleFilter.AnyDps => MeleeJobs.Contains(job) || PhysicalRangedJobs.Contains(job) || CasterJobs.Contains(job),
        _ => true,
    };

    public static string DisplayName(this RoleFilter role) => role switch
    {
        RoleFilter.AnyRole => "Any role",
        RoleFilter.Tank => "Tank",
        RoleFilter.Healer => "Healer",
        RoleFilter.Melee => "Melee DPS",
        RoleFilter.PhysicalRanged => "Physical Ranged DPS",
        RoleFilter.Caster => "Caster DPS",
        RoleFilter.AnyDps => "Any DPS",
        _ => "Any role",
    };
}
