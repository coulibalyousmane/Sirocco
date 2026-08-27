using System.Runtime.CompilerServices;

// Le plan de modele de charge (StandaloneHost.BuildLoadModel, LoadModelPlan) reste interne : rien
// en dehors de l'hote n'a a le construire. Les tests, eux, en ont besoin — verifier la capacite de
// file imposee aux modeles tires en la recopiant a la main ne prouverait rien, puisque la valeur
// recopiee cesserait de suivre le code des le premier changement.
[assembly: InternalsVisibleTo("Sirocco.UnitTests")]