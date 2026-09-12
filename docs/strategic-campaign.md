# Continuité stratégique

La commande `run-campaign --session FILE --max-goals 10` enchaîne des objectifs libres du modèle configuré. Elle conserve un seul bail de contrôle du personnage pour toute l'exécution. La borne est réglable de 1 à 10000 objectifs ; son épuisement est un arrêt de budget, jamais une réussite de campagne.

Chaque itération observe le moteur, lit le catalogue natif et transmet au modèle le résultat vérifié précédent comme donnée historique. Les stocks, recettes, positions et préconditions sont relus par les exécuteurs C# avant leurs actions. Le texte libre du modèle ne constitue aucune preuve d'exécution. Les résultats de recherche et de production restent distincts.

Le fichier local ignoré `strategic-memory.json` est remplacé atomiquement dans le dossier de session. Il contient l'identité du monde et de l'acteur, le tick, un indicateur d'exécution en cours et le dernier résultat borné à 4000 caractères. Le dernier résultat survit au redémarrage du processus et au changement de session serveur. Un monde différent, une incarnation différente, une horloge antérieure ou un changement de portée pendant une exécution provoquent un arrêt explicite.

L'indicateur en cours est écrit avant l'appel au contrôleur d'objectif et effacé seulement après son retour et une observation finale cohérente. Une exception, une annulation ou une issue inconnue laisse cet indicateur actif : une nouvelle commande ne répète pas aveuglément l'exécution. La réconciliation automatique complète de ce cas reste à implémenter ; il faut actuellement examiner le journal et les effets natifs. Effacer ce fichier sans cette vérification ferait perdre l'information d'incertitude.

Les propositions refusées par la validation sémantique reviennent comme résultats `unsupportedReason` au modèle. Trois propositions successives de même catégorie, cible, quantité et unité arrêtent la boucle avec `repeated-goal`. Les erreurs survenant pendant l'exécution ne sont pas assimilées à ces refus : elles arrêtent la boucle avec l'indicateur en cours conservé.

La boucle signale `rocket-observed` uniquement lorsque le compteur natif de fusées est strictement positif. Cela constate un lancement dans le monde ; cela ne qualifie pas à lui seul l'historique, la graine, l'absence d'assistance ou les trois campagnes finales.

## État de validation

Les tests hors ligne vérifient le transfert du résultat précédent entre objectifs, la persistance entre instances, l'arrêt sur fusée native, l'arrêt de budget, le refus d'une nouvelle exécution après timeout, les mondes ou dates incompatibles, une opération native encore active, les changements de portée pendant une exécution et la détection des propositions répétées. Le contrôleur stratégique concret transmet les objectifs scientifiques et renvoie les propositions non prises en charge sans effectuer leurs actions.

Deux objectifs successifs ont été qualifiés avec le modèle réel dans une fixture, comme décrit ci-dessous. Cette commande ne comble pas les limitations actuelles de production des fluides, de transport industriel, de défense et de récupération de campagne.

## Essai réel de deux objectifs

Sur la fixture explicitement approvisionnée de Factorio 2.0.77, `glm-5.3-flash:cloud` a choisi successivement la recherche `electric-mining-drill`, puis un stock porté de 50 packs rouges. Le premier appel a pris 1,513 seconde côté client (5063 tokens d’entrée, 85 de sortie), le second 2,067 secondes (5231 et 124 tokens). Le deuxième contexte contient le résultat natif vérifié de la première recherche dans `previousResult`.

La recherche a terminé entre les ticks 146990 et 162490, avec 25 packs consommés et 193 relevés de laboratoire alimenté. La deuxième exécution a fabriqué 49 packs à partir de 49 plaques de cuivre et 49 engrenages, portant le stock de 1 à 50 entre les ticks 162647 et 177421. Le fichier de mémoire au tick 177424 contient ce résultat avec `pending=false`. Une lecture indépendante au tick 178331 retrouve les 50 packs et le personnage à 250 points de vie.

La commande s’est arrêtée normalement avec deux objectifs exécutés, `stopReason=goal-budget` et `rocketLaunched=false`. Aucun objectif n’a été choisi ou corrigé à la main entre ces deux appels. Les ressources initiales artificielles de la fixture interdisent de compter cet essai comme une campagne normale. La reprise après résultat incertain et la progression complète jusqu’à la fusée restent à développer et qualifier.
