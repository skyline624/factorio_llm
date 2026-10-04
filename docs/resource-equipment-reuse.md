# Réemploi des équipements d'une cellule épuisée

Avant de fabriquer les équipements manquants d'une nouvelle cellule, `FactoryCellBuilder` cherche des foreuses et fours récupérables dans les cellules `depleted`. Le besoin vient du plan C# et des objets réellement portés. La récupération reste bornée à quatre équipements et à 96 tuiles autour du personnage ; le four précède la foreuse.

Chaque candidat doit correspondre à une identité native connue, une position et un prototype enregistrés, avec une seule cellule propriétaire. Une foreuse encore présente doit signaler `no_minable_resources`. Le four doit être sans cuisson engagée, sans intrant ni produit, sans fluide ou transit chargé. Ses inventaires doivent être observés et les piles observées de qualité normale. Les connexions rouges et vertes doivent être explicitement absentes : une donnée manquante refuse le démontage.

Après le déplacement, le contrôleur relit la cellule et les stocks. Une machine étrangère qui alimente le four empêche sa récupération. Le combustible est d'abord transféré au personnage par des opérations natives, puis un nouveau relevé confirme les conditions de démontage. Le minage du bâtiment doit avoir un reçu terminé et concordant, faire disparaître son identité native et augmenter le stock porté de l'équipement attendu. Ce n'est qu'ensuite que son rôle est retiré du registre. Le coffre, ses produits, les bras et les poteaux restent en place ; la cellule reste `depleted`.

La réinstallation suit la synthèse ordinaire de `ResourceCellPlanner` et les coûts natifs. Un résultat inconnu interrompt le chemin pour réconciliation. Avoir déjà le kit dans le sac empêche un nouveau démontage inutile.

## Qualification préparée

`verify-resource-equipment-reuse --session FILE` refuse une session normale avant toute préparation. Le scénario fournit explicitement le terrain, deux gisements de fer, l'énergie, des recherches et le kit initial. C# construit la première cellule ; la fixture supprime ensuite le minerai sous sa foreuse et ajoute temporairement un circuit vert pour vérifier le refus de récupération.

Le 4 octobre 2026, avec Factorio **2.0.77 headless**, sans client connecté, graine préparée **20261161** :

- la foreuse reliée par le circuit vert reste en place ;
- après déconnexion, deux opérations natives récupèrent le four et la foreuse ;
- les **47 charbons** encore en inventaire sont conservés pendant la récupération, ainsi que les **5 plaques** produites dans l'ancien coffre ;
- le kit réinstallé produit **14 plaques** sur le second site au tick **5287** ;
- le journal contient exactement deux minages des équipements concernés, aucune fabrication ni extraction manuelle de minerai, et aucun démontage répété lorsque le kit est déjà porté ;
- le serveur préparé est sauvegardé et arrêté.

Les **1 562 tests offline** passent également, sans jeu ni cloud. Le premier essai natif refusait une raison de fixture trop longue ; le second révélait l'absence des champs de circuit vert dans le contrat spatial C#. Ces défauts ont été corrigés et les échecs conservés dans les données privées.

Ce résultat qualifie le composant sur une préparation explicite. Il ne mesure pas encore le gain de durée dans une partie normale et ne prouve aucune progression jusqu'à la fusée. Les cellules éloignées, les fours encore chargés et les équipements raccordés à un circuit restent exclus de ce chemin. Les coffres, bras et poteaux d'un ancien site ne sont pas réemployés ici.

## Observation dans une partie normale

Le 4 octobre 2026, sur la graine normale **20261072**, sans préparation de ressources ni pilote connecté, les reçus natifs confirment la récupération d'un four en acier au tick **2379806**, puis d'une foreuse électrique au tick **2380187**. Les **1 264 plaques de fer** de l'ancien coffre restent présentes après cette récupération.

Le kit est réinstallé dans une cellule de cuivre sans fabriquer de four ou de foreuse de remplacement dans la séquence de récupération et de construction. Le premier relevé trouve la foreuse sans courant ; après reprise de l'alimentation, le relevé natif au tick **2450444** constate **105 fabrications terminées** par le four et **105 plaques de cuivre** dans le nouveau coffre. Les objets sont fongibles : les reçus, le bilan de stock et l'absence de fabrication de remplacement établissent le réemploi, sans numéro de série par objet.

Cette observation établit un réemploi productif dans le monde normal. Elle ne mesure pas encore un gain de temps, un débit durable ni un lancement de fusée. Les captures et le préfixe du journal ayant servi à la vérification restent dans les données privées `.runtime/`.
