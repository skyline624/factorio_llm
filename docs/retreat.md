# Repli vers une tourelle observée

La défense C# recherche un repli lorsqu'un ennemi est visible et que le personnage est à 40 % de sa santé maximale native ou moins, ou qu'il n'a ni arme prête ni équipement porté permettant de se réarmer. Cette décision exige une tourelle propre, active, chargée, actuellement observée et une liste locale d'ennemis non tronquée. Une tourelle vide n'est pas proposée comme couverture.

Le mod fournit la position, la portée native applicable et les cartouches d'une tourelle à balles de qualité normale. C# lit ensuite la géométrie locale et vérifie l'identité et la date des observations. Il cherche une destination dans la moitié intérieure de la portée d'une tourelle, avec les boîtes de collision et masques du moteur. Les routes qui réduisent trop la séparation initiale avec les ennemis observés sont écartées. Aucun gabarit, aucune position LLM et aucun espace inconnu supposé libre ne sont utilisés.

La recherche teste au plus douze destinations ; chaque recherche A* possède ses propres limites de temps et de nœuds. Un budget épuisé ou l'absence de candidat acceptable n'est pas une preuve d'impossibilité globale. Seul le prochain déplacement, long de deux cases au maximum, est soumis au moteur. Le contrôleur observe à nouveau après ce déplacement et recalcule le repli. Les régions parcourues par la direction native sont vérifiées pour éviter les coins d'obstacles.

La préemption, les déplacements et les tirs passent par le même arbitre et le même journal. Une opération de repli sans réponse est interrogée par identité avant toute autre action ; aucun déplacement ambigu n'est renvoyé aveuglément. Une session différente, un terrain trop ancien ou un changement de position inattendu interdit la soumission.

## Essais natifs du 13 septembre 2026

`verify-retreat --session FILE` exige une fixture et refuse les campagnes normales. La préparation place un personnage désarmé et blessé à l'origine, une tourelle à `(-24, 0)` avec 100 cartouches, un mur à contourner et un petit déchiqueteur poursuivant depuis `(8, 0)`. Le scénario vérifie d'abord que la tourelle vide ne figure pas dans les défenses disponibles. Il prépare une attente représentant le travail à interrompre. Les déplacements de fuite sont calculés par le contrôleur réel, sans direction fournie par le scénario.

| Mode | Ticks avant/après | Position finale | Santé finale | Plans / déplacements terminés | Cartouches de tourelle restantes |
| --- | --- | --- | --- | --- | --- |
| Headless | 88484 → 88708 | (-10,71 ; 1,48) | 69,95 | 7 / 6 | 96 |
| Pilote connecté | 94390 → 94684 | (-11,47 ; 1,16) | 63,85 | 7 / 7 | 96 |

Les deux essais constatent l'attente annulée, le contournement du mur, le même personnage 224 et la même incarnation 7, un personnage survivant sans munition portée, quatre cartouches consommées par la tourelle et la disparition du poursuivant. Le nombre de déplacements terminés peut être inférieur au nombre de plans : le dernier déplacement est arrêté lorsque le danger a disparu. La santé initialement préparée à 80 peut déjà avoir régénéré lors de la première lecture native ; les rapports conservent ces valeurs exactes.

Le joueur connecté reste attaché au même personnage. Une capture native montrant le personnage, le mur, la tourelle et le corps du poursuivant a été inspectée. Les rapports privés sont `7510bffedd2f4e6b817be290286b0f3f` et `4728865c45eb4195a960190cf650797e`. Le scénario de réarmement a également été rejoué avec succès avec ce contrôleur (`6e8755c9a3834abcb657a81b9e075094`).

## Limites

Ces scénarios préparés ne sont pas des campagnes autonomes et n'utilisent pas d'inférence cloud. Ils qualifient le repli vers une couverture existante contre un poursuivant, pas la fuite en terrain ouvert ni la survie face à toutes les attaques. Une tourelle chargée n'est pas la garantie qu'elle possède assez de munitions ou de puissance pour vaincre chaque ennemi. La construction préventive de positions défensives, leur approvisionnement durable, les menaces simultanées et l'évaluation de leur force restent à compléter. Sans route acceptable vers une couverture observée, le contrôleur conserve ses autres actions disponibles, sans prétendre avoir trouvé un lieu sûr.
