using Sirocco.Domain.Load;

namespace Sirocco.Host.Configuration;

/// <summary>Traduit la configuration <c>Sirocco:RampVus</c> en <see cref="VirtualUserProfile"/>.</summary>
internal static class VirtualUserProfileFactory
{
    /// <summary>Construit le profil de montee d'utilisateurs decrit par les options.</summary>
    /// <exception cref="ArgumentException">Aucun palier n'est configure.</exception>
    public static VirtualUserProfile FromOptions(SiroccoHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return FromStages(options.RampVus);
    }

    /// <summary>
    /// Construit un profil de montee d'utilisateurs a partir d'une liste de paliers deja
    /// resolue, meme convention que <see cref="LoadProfileFactory.FromStages"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Aucun palier n'est fourni.</exception>
    public static VirtualUserProfile FromStages(IReadOnlyList<VirtualUserStageOptions> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);

        if (stages.Count == 0)
        {
            throw new ArgumentException("Au moins un palier de montee d'utilisateurs doit etre fourni.", nameof(stages));
        }

        return new VirtualUserProfile(stages.Select(static stage =>
            VirtualUserStage.Ramp(stage.FromVus, stage.ToVus, TimeSpan.FromSeconds(stage.DurationSeconds))));
    }
}