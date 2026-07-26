// Les tests partagent l'état statique de Domaines (chargement de config) :
// on désactive la parallélisation pour éviter toute course entre classes de tests.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
