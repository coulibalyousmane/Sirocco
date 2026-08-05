using Tempest.Domain.Load;

namespace Tempest.Host.Configuration;

/// <summary>Traduit la configuration <c>Tempest:Profile</c> en <see cref="LoadProfile"/>.</summary>
internal static class LoadProfileFactory
{
    /// <summary>Construit le profil de charge decrit par les options.</summary>
    /// <exception cref="ArgumentException">Aucun palier n'est configure.</exception>
    public static LoadProfile FromOptions(TempestHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return FromStages(options.Profile);
    }

    /// <summary>
    /// Construit un profil de charge a partir d'une liste de paliers deja resolue — celle de
    /// l'hote lui-meme en mode autonome/maitre, ou celle, dejar reduite par worker, recue via
    /// <c>/worker/prepare</c> en mode distribue.
    /// </summary>
    /// <exception cref="ArgumentException">Aucun palier n'est fourni.</exception>
    public static LoadProfile FromStages(IReadOnlyList<LoadStageOptions> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);

        if (stages.Count == 0)
        {
            throw new ArgumentException("Au moins un palier de charge doit etre fourni.", nameof(stages));
        }

        return new LoadProfile(stages.Select(static stage =>
            LoadStage.Ramp(stage.FromRps, stage.ToRps, TimeSpan.FromSeconds(stage.DurationSeconds))));
    }
}