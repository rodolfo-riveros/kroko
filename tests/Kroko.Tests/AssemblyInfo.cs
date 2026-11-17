// Las claves de IA y la IA de la PC viven en memoria compartida del cerebro (AppProvidedKeys, LocalLlm): las pruebas se
// ejecutan una tras otra para que ninguna vea el estado de otra (17/11/2026). Todas juntas tardan unos 2 segundos.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
