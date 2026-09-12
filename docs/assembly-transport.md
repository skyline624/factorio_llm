# Assemblage avec approvisionnement par tapis

La commande `assemble` recherche les sources solides propres et connues de chaque ingrédient. Elle réutilise une liaison native vérifiée ou calcule en C# une nouvelle liaison locale. La préparation du trajet et la preuve de livraison sont séparées : plusieurs flux peuvent être observés pendant que la machine consomme ses ingrédients.

## Comptabilité et réservations

Chaque flux conserve sa photographie initiale et vérifie les inventaires, les coffres intermédiaires, les voies de tapis, les mains des bras et les cycles natifs. Les bilans amont et aval doivent concorder. Une donnée native absente reste une erreur ; une source incompatible est simplement écartée. Un coffre alimenté par la sortie de la machine cible ne peut pas être proposé comme son entrée.

Un coffre vide peut rester une source pertinente si son producteur est engagé ou si les ingrédients nécessaires à un cycle sont déjà présents dans ses entrées et ses liaisons entrantes. Cette vérification exige une machine électrique alimentée et les fluides nécessaires déjà disponibles. Elle distingue cette capacité de produire du stock réellement disponible ; elle ne compte aucun produit futur comme livré.

Les entités des flux suivis sont réservées pendant les fabrications imbriquées et le ravitaillement électrique. Ces réservations suivent la pile d’appels asynchrones ; elles ne constituent pas encore un ordonnanceur industriel durable. Un flux épuisé est clôturé avant une livraison explicite du même ingrédient par le personnage.

Si une liaison de sortie vers un coffre existe, la collecte passe par ce coffre même lorsqu’il est temporairement vide. Les produits présents dans la machine et en transit réduisent le besoin restant. Le calcul est renouvelé avant chaque ingrédient et après une fabrication imbriquée. La réussite de `assemble` exige toujours le stock porté demandé et un reçu natif de collecte ; un produit sur un tapis ne suffit pas.

## Essais Factorio 2.0.77

Les essais prolongent la fixture de graine **424245** décrite dans [le transport solide](belt-transport.md). Son énergie, ses recherches et ses premiers équipements sont artificiels. Aucun engrenage ni pack rouge n’a été injecté. Ils ne qualifient aucune campagne autonome.

Un premier essai a ajouté une alimentation en cuivre : source choisie dans un nouveau coffre préparé avec dix plaques, puis quatre tapis, deux bras et un poteau construits par le contrôleur. Les deux entrées étaient suivies simultanément. La collecte a toutefois échoué au tick **108258** : un bras avait vidé la sortie de la machine avant le transfert du personnage. Cette commande n’est pas comptée comme réussie.

Les essais suivants ont révélé la mauvaise interprétation d’un coffre de sortie comme source candidate, puis des fabrications imbriquées inutiles alors que les ingrédients ou les produits étaient déjà en transit. Deux contrôleurs ont été interrompus explicitement ; les opérations ont été annulées, les mondes sauvegardés et repris sans restaurer un état antérieur. Un four construit et les effets des fabrications restent dans le monde. Ces interventions sont une assistance de développement, pas une récupération autonome qualifiée. Les corrections sont couvertes par des tests ciblés.

Les apports complémentaires de cette série sont trois lots de vingt plaques de fer et dix de cuivre, puis cinq plaques de cuivre entre les deux derniers lots. Une reprise a collecté trois packs déjà produits, portant le stock du personnage à 65 au tick **188783** ; elle n’a terminé aucun nouveau cycle.

### Nouveau lot terminé sans intervention pendant la commande

Une lecture indépendante au tick **213554** constate 65 cycles d’engrenages et 65 cycles scientifiques terminés, 65 packs portés et aucun engrenage ni pack en transit. Au tick **213555**, la préparation ajoute vingt plaques de fer au coffre amont et dix de cuivre à sa source. Aucun ingrédient n’est inséré dans une machine par le script.

La commande `assemble --item automation-science-pack --quantity 70` s’exécute entre les ticks **213618 et 217583** :

| Preuve | Résultat |
| --- | --- |
| Stock porté initial → final | 65 → 70 packs rouges |
| Cycles scientifiques terminés pendant la commande | 5 |
| Sources retenues | Coffre de cuivre et coffre d’engrenages ravitaillé |
| Engrenages disponibles lors de la sélection | 0 ; ingrédients amont déjà acheminés |
| Observations des flux cuivre / engrenages | 89 / 88 |
| Livraisons vérifiées depuis leurs photographies respectives | 6 cuivres / 8 engrenages |
| Actions soumises | 8 mouvements, 27 attentes, 5 collectes |
| Minage, fabrication imbriquée, insertion d’ingrédients, nouvelle construction | Aucun |

Les livraisons ne sont pas toutes déjà consommées à la fin de la commande. Les machines continuent de fonctionner ensuite : au tick **219351**, la lecture indépendante constate 75 engrenages produits, 73 packs produits, un cycle scientifique engagé, un engrenage encore en stock et aucun fer, engrenage ou pack en transit. Les 73 packs sont répartis entre les 70 portés et les trois du coffre de sortie. Les statistiques natives indiquent 150 plaques de fer consommées, 74 engrenages et 74 plaques de cuivre consommés ; le cycle engagé explique l’écart d’une unité avec les 73 packs terminés. Le personnage possède 250 points de vie et aucun joueur n’est connecté.

Les cinq liaisons conservent 27 tapis et dix bras, sans reconstruction pendant ce lot. Le monde est sauvegardé et arrêté au tick **219411**. Les journaux et sauvegardes restent privés. La compilation finale des huit projets réussit sans avertissement ; **379 tests passent**, et le test cloud optionnel reste ignoré dans cette suite hors ligne.

## Limites restantes

La sélection et les routes sont locales, bornées et calculées successivement. La création et le remplissage automatiques de toutes les sources, le calcul conjoint de toutes les entrées, les branchements industriels généraux et la reprise durable d’une ligne détruite restent à intégrer. Faute de source exploitable, la production peut encore utiliser le personnage pour fournir un ingrédient. Un apport externe dans un flux déjà suivi exige une réconciliation.

Cette preuve établit l’utilisation de deux alimentations par tapis dans la commande générale d’assemblage. Elle ne démontre ni une usine complète créée depuis le départ normal, ni une attaque sur ces liaisons, ni la chaîne de la fusée. Les trois campagnes finales restent à réaliser.
