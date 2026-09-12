# Continuité stratégique

La commande `run-campaign --session FILE --max-goals 10` enchaîne des objectifs libres du modèle configuré. Elle conserve un seul bail de contrôle du personnage pour toute l'exécution. La borne est réglable de 1 à 10000 objectifs ; son épuisement est un arrêt de budget, jamais une réussite de campagne.

Chaque itération observe le moteur, lit le catalogue natif et transmet au modèle le résultat vérifié précédent comme donnée historique. Les stocks, recettes, positions et préconditions sont relus par les exécuteurs C# avant leurs actions. Le texte libre du modèle ne constitue aucune preuve d'exécution. Les résultats de recherche et de production restent distincts.

Le fichier local ignoré `strategic-memory.json` est remplacé atomiquement dans le dossier de session. Il contient l'identité du monde et de l'acteur, le tick, un indicateur d'exécution en cours et le dernier résultat borné à 4000 caractères. Le dernier résultat survit au redémarrage du processus et au changement de session serveur. Un monde différent, une incarnation différente, une horloge antérieure ou un changement de portée pendant une exécution provoquent un arrêt explicite.

L'indicateur en cours est écrit avant l'appel au contrôleur d'objectif et effacé seulement après son retour et une observation finale cohérente. Une exception, une annulation ou une issue inconnue laisse cet indicateur actif : une nouvelle commande ne répète pas aveuglément l'exécution. La réconciliation automatique complète de ce cas reste à implémenter ; il faut actuellement examiner le journal et les effets natifs. Effacer ce fichier sans cette vérification ferait perdre l'information d'incertitude.

Les propositions refusées par la validation sémantique reviennent comme résultats `unsupportedReason` au modèle. Trois propositions successives de même catégorie, cible, quantité et unité arrêtent la boucle avec `repeated-goal`. Les erreurs survenant pendant l'exécution ne sont pas assimilées à ces refus : elles arrêtent la boucle avec l'indicateur en cours conservé.

La boucle signale `rocket-observed` uniquement lorsque le compteur natif de fusées est strictement positif. Cela constate un lancement dans le monde ; cela ne qualifie pas à lui seul l'historique, la graine, l'absence d'assistance ou les trois campagnes finales.

## État de validation

Les tests hors ligne vérifient le transfert du résultat précédent entre objectifs, la persistance entre instances, l'arrêt sur fusée native, l'arrêt de budget, le refus d'une nouvelle exécution après timeout, les mondes ou dates incompatibles, une opération native encore active, les changements de portée pendant une exécution et la détection des propositions répétées. Le contrôleur stratégique concret transmet les objectifs scientifiques et renvoie les propositions non prises en charge sans effectuer leurs actions.

L'enchaînement de plusieurs objectifs avec le modèle réel dans le jeu reste à qualifier. Cette commande ne comble pas les limitations actuelles de production des fluides, de transport industriel, de défense et de récupération de campagne.
