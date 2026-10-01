# Pilotage du silo et preuve de lancement

`launch-rocket --session FILE --item rocket-silo` vise un lancement supplémentaire. Le modèle peut proposer le même objectif avec la catégorie `launch`, l'unité `completion`, la quantité 1 et un identifiant natif d'objet de silo. Les recettes doivent être débloquées auparavant par les objectifs de recherche.

Le contrôleur C# réutilise un silo propre connu alimenté, en privilégiant une fusée prête puis le plus grand nombre de pièces. À défaut, il passe par la fabrication et le placement calculé des machines électriques. Le solveur prend en compte l'empreinte native du silo et la couverture du poteau. Cette installation reste limitée à la zone observée et à une extension locale de poteau. Avec le registre d'usine, une [cellule de silo persistante](#cellule-de-silo-persistante) passe avant tout silo isolé.

La lecture Lua `rocket_state` fournit à un tick unique le compteur de lancements de la force, les silos propres connus sur la surface du personnage, leurs stocks d'entrée, les capacités d'insertion, l'énergie, le réseau, le nombre de pièces, la fabrication engagée, la présence de la fusée et la phase native. Elle inclut la recette fixe et les paramètres du prototype fourni par le jeu. C# refuse les observations incohérentes, incomplètes ou d'une autre identité d'acteur.

Le bilan retire les pièces déjà fabriquées, le cycle engagé et les ingrédients chargés. Chaque livraison représente au plus cinq cycles et reste bornée par la capacité native et le budget de production. Le silo est réservé pendant la collecte des ingrédients pour empêcher leur récupération par une production imbriquée. Le personnage collecte les ingrédients du petit lot avant de revenir au silo ; les quantités sont recalculées après les déplacements et la fabrication. Les besoins de matières empruntent les contrôleurs existants, avec priorité aux stocks et aux machines.

Le compteur de pièces peut revenir à zéro pendant la préparation ou le lancement : cette transition ne déclenche pas un nouvel approvisionnement. Seule la phase native `rocket_ready`, avec une entité de fusée présente et une vérification après le déplacement, autorise l'action `launch_rocket`. Son reçu doit se terminer, puis une lecture indépendante doit constater l'augmentation du compteur de lancements. Une annulation ou une réponse inconnue exige une réconciliation avant toute reprise.

## Premier essai natif, scénario préparé

Factorio 2.0.77 de base, scénario explicitement marqué comme fixture. La préparation apporte un silo, une source électrique artificielle, les recherches, 98 pièces et 20 unités de chacun des trois ingrédients. Une erreur de couverture électrique du montage a d'abord provoqué un arrêt explicite ; un second poteau de test a ensuite raccordé la source.

L'exécution C# a réussi entre les ticks 3 864 et 6 591. Elle a transféré les ingrédients des deux dernières pièces, attendu la préparation et lancé une fusée sans cargaison. Le reçu de lancement accepté au tick 5 332 s'est terminé au tick 6 582. La relecture au tick 6 923 confirme le compteur passé de 0 à 1, les entrées vides et le silo revenu à la fabrication avec zéro pièce. Aucun remplissage supplémentaire n'a été déclenché après le lancement.

## Cycle complet avec stocks préparés

Le même silo, revenu à zéro pièce, a reçu ses ingrédients depuis cinq coffres de test contenant au total 1 000 unités de chacun des trois ingrédients. Le contrôleur a exécuté tout le cycle entre les ticks 9 696 et 121 538. La simulation de cette seule fixture est passée de vitesse 1 à vitesse 4 au tick 28 765 ; aucun ingrédient ni progrès de fabrication n'a été ajouté pendant l'exécution.

Les reçus totalisent exactement 1 000 unités transférées de chaque ingrédient. Le lancement accepté au tick 120 264 s'est terminé au tick 121 514. La lecture indépendante au tick 126 050 constate deux lancements cumulés, aucun joueur connecté et aucun reste de ces ingrédients dans le personnage ou les coffres. Le silo n'a plus d'entrée. Aucun minage ni fabrication manuelle n'a été soumis.

Cette version faisait encore un aller-retour par ingrédient : le journal compte 2 524 déplacements, 192 collectes et 194 insertions. Ce coût motive la collecte groupée des petits lots ; le transport industriel continu reste à développer.

## Vérification reproductible

`verify-rocket --session FILE` exige un manifeste de session explicitement créé avec `--fixture`. Cette commande remplace le terrain et les machines de sa zone de test, fournit une source électrique artificielle et les recherches, prépare un silo avec 96 pièces et trois coffres contenant chacun 40 ingrédients. Elle rétablit la vitesse normale et accepte le pilote du même personnage connecté. Elle vérifie un lancement supplémentaire et la disparition des ingrédients préparés, puis écrit un rapport distinguant succès, preuves natives et caractère artificiel du scénario.

La commande et la livraison groupée ont réussi en headless entre les ticks 126 408 et 130 122 : compteur de 2 à 3 et zéro reste des trois ingrédients au tick 130 126. L'essai répété avec un unique client graphique connecté au même avatar a réussi entre les ticks 138 560 et 142 400 : compteur de 3 à 4, un joueur connecté et zéro reste au tick 142 408. Le client a été lancé avec une fenêtre réduite. Une capture a été obtenue directement par `game.take_screenshot` dans son dossier local, sans clic d'interface ; elle reste hors du dépôt avec les autres données du jeu. Ces deux exécutions sont à vitesse normale et leurs rapports indiquent explicitement `isAutonomousCampaign=false`.

Ces preuves portent sur un silo existant et une économie préparée. Elles ne qualifient ni sa fabrication intégrale, ni son installation native par ce contrôleur, ni la production normale de tous les ingrédients. Le dimensionnement électrique, le débit logistique et les budgets de production restent limitants. Le contrôleur est borné à deux heures et 7 200 observations ; les sous-objectifs conservent leurs propres délais. Aucune campagne autonome jusqu'à la fusée n'est encore qualifiée.

## Cellule de silo persistante

Les essais précédents alimentent un silo isolé à la main. Ce module fait du silo une cellule de l'[usine persistante](factory.md) : la logistique remplit son coffre, son bras charge le silo, la maintenance reconstruit ses pièces et C# lance depuis elle.

### Cellule

- Le registre connaît un nouveau type de cellule, `silo`. Ses rôles sont `machine` (le silo), `input-inserter`, `input-chest` et `pole`, puis `link-n` si un raccordement est nécessaire. Chaque rôle garde son plan, donc `FactoryMaintenance` le reconstruit à sa position et dans son orientation.
- La cellule occupe une bande à elle, d'un seul emplacement (`FactoryCellBuilder.Slots`). La géométrie native du silo (9 × 9 cases) donne un pas de 9 cases et une hauteur de bande de 24. `FactoryZonePlanner` cherche donc un rectangle de 11 × 26 cases près du réseau alimenté, comme pour toute bande. Cette bande est réservée comme les autres : liaisons électriques, cellules fluides et croissance de la vapeur l'évitent. `FactoryBandPlanner` place le silo, puis sous lui le bras, le coffre et le poteau. La direction du bras vient des vecteurs natifs de prise et de dépose.
- La cellule n'a pas de côté de sortie (`FactoryCellBuilder.Sides`) : une pièce de fusée ne quitte jamais le silo sous forme d'objet, il n'y a rien à collecter.
- La recette `rocket-part` est fixée par le prototype (`fixed_recipe`). Ni la construction ni la maintenance n'envoient de `set_recipe` (`FactoryCellBuilder.Configured`).
- Un seul coffre partagé et un seul bras suffisent, au vu du débit natif. Un bras de base déplace environ 0,8 objet par seconde (`AutomationPlanner.InserterItemsPerSecond`) ; l'essai mesure 30 objets en 2 150 ticks environ, soit 0,83 objet par seconde. Une pièce demande 30 objets : la cellule fait donc au plus 1,6 pièce par minute, une fusée de 100 pièces en un peu plus d'une heure. Le silo, lui, fabriquerait nativement 20 pièces par minute. Les chaînes qui alimentent la cellule livrent bien moins : 1,6 pièce par minute demanderait déjà 16 unités de traitement par minute, donc 320 circuits électroniques. Un coffre et un bras par ingrédient ne serviraient qu'au-delà. Le bras ne prend dans le coffre que ce que le silo accepte encore : un coffre mélangé ne bloque pas.
- Une usine ne construit qu'un silo (`SiloCellPlanner.MaximumCells`). Le second emplacement de la bande reste libre.

### Planification

- Le catalogue de production exporte les prototypes de silo (`silos`) : recette fixe, nombre de pièces, vitesse de fabrication et énergie. Ils viennent de la même fonction Lua que ceux de `rocket_state`.
- `AutomationPlanner` traite le silo comme une étape. `rocket-part` est servie par le silo dont c'est la recette fixe, dès que la recette du silo est débloquée : `FactoryDirector.MachineItems` ajoute alors le silo au meilleur assembleur et au four. Une cellule vaut la vitesse native bornée par le bras, soit 1,6 pièce par minute. Une étape de silo ne compte jamais plus d'une cellule, quel que soit le débit demandé.
- `FactoryDirector.AutomateAsync("rocket-part", débit)` enregistre la cible, planifie toutes les cibles ensemble et construit la cellule comme les autres. L'énergie vient d'abord : l'extension de vapeur compte la consommation maximale native du silo, 66 500 J par tick (3,99 MW) d'après `get_max_energy_usage`, soit sa consommation active pendant les phases de lancement.
- Les ingrédients se planifient derrière le silo. La structure basse densité, faite de solides, devient une étape d'assembleur, et son acier une étape de four. Le cuivre et le plastique restent des matières premières ; le plastique a sa propre [chaîne fluide](oil-chemistry.md), que ce plan ne déclenche pas. Les unités de traitement et le carburant de fusée exigent des assembleurs alimentés en fluide, pas encore automatisés. Ils restent donc des matières premières du plan (`rawPerMinute`), signalées au modèle dans le résultat d'automatisation, et la production pilotée les procure.
- Exemple journalisé par le moteur, toutes recherches faites : une pièce par minute donne 6 assembleurs 2 de structures (10 par minute) et 3 fours en acier (20 aciers par minute). Restent bruts, par minute : 10 unités de traitement, 10 carburants, 200 plaques de cuivre, 100 plaques de fer et 50 plastiques.
- `StrategicProductionController` accepte donc un objectif `items_per_minute` pour `rocket-part` dès que le silo est recherché. Il compte les cellules de silo dans les faits envoyés au modèle et décrit ces deux chemins dans les capacités d'exécution.

### Logistique

`FactoryLogistics` remplit le coffre du silo comme celui d'un assembleur, avec le tampon planifié : dix minutes de la part de la cellule, entre 5 et 40 pièces (`FactoryLogistics.CellBufferCrafts`). Une matière rare est d'abord répartie pour que chaque coffre atteigne le quart de sa cible, et le manque est signalé. Rien n'est collecté.

### Lancement depuis la cellule

- Avec le registre (`launch-rocket`, objectif stratégique `launch`), `RocketLaunchController` choisit d'abord la cellule prête dont le silo est observé : une fusée prête d'abord, puis le plus de pièces (`SiloCellSupply.Registered`). Le silo d'une cellule, prête ou non, n'est jamais choisi comme silo isolé.
- Si aucun silo alimenté n'existe et que l'automatisation est disponible, le contrôleur construit (ou reprend) la cellule au lieu d'installer un silo isolé, après l'extension électrique. Sinon, l'ancien chemin s'applique.
- En mode cellule, rien n'est jamais inséré à la main dans le silo. Tant que la phase native demande des ingrédients :
  1. C# relit la cellule dans le registre, pour suivre un coffre ou un bras reconstruit.
  2. Il lit le stock de la cellule (coffre et main du bras), puis l'étape du silo. Un ingrédient déposé entre-temps compte donc comme chargé : il n'est jamais procuré deux fois.
  3. Il procure seulement ce qui manque pour au plus cinq cycles (`SiloCellPlanner.Procurement`, `RocketPlanner.BatchCycles`), puis sert toute la logistique de l'usine.
- Quand la cellule tient déjà le lot, le contrôleur attend 60 ticks et observe de nouveau. La maintenance électrique, le lancement sur `rocket_ready` relu après déplacement, la vérification du compteur et l'arrêt sur un reçu inconnu sont inchangés. Les cellules du registre restent réservées contre la production imbriquée, coffre du silo compris.

```text
automate --session FILE --item rocket-part --quantity 1
launch-rocket --session FILE --item rocket-silo
verify-silo-cell --session FILE
```

### Qualification préparée `verify-silo-cell`

La commande exige une session fixture marquée. Les étapes artificielles sont listées une par une dans le rapport (`artificial`), qui porte `isAutonomousCampaign=false` :

- la zone de -48 à 48 est vidée et couverte d'herbe, et le personnage, inventaire vidé, est placé en (0, 0) ;
- une interface électrique est injectée en (-20, 0), avec son poteau en (-18,5 ; 0,5) ;
- toutes les recherches sont annulées, puis seules `electronics`, `automation` et `rocket-silo` sont accordées et leurs effets réappliqués ;
- le personnage reçoit 1 silo, 2 bras, 2 coffres en fer et 12 petits poteaux ;
- le registre est supprimé.

L'essai enchaîne ensuite quatre phases :

1. **Construction par le directeur.** `AutomateAsync("rocket-part", 1)` doit planifier une seule étape de silo et signaler les trois ingrédients bruts, à dix par minute chacun. La cellule doit être exactement un silo, un coffre, un bras et un poteau, avec un plan, dans une bande d'un emplacement.
2. **Dernière pièce et lancement.** Après la construction, la fixture fixe les pièces du silo à 99 sur les 100 natives (`rocket_parts`). Elle donne au personnage, qui n'en porte aucun, exactement les ingrédients natifs d'une pièce. L'essai vérifie d'abord nativement que le bras prend dans le coffre et dépose dans le silo. Puis le lancement depuis la cellule doit monter le compteur d'exactement un, relu indépendamment. Le silo doit finir exactement une pièce (`products_finished`), et les ingrédients doivent avoir été consommés : personnage, coffre, silo et main du bras vides. La logistique doit avoir mis exactement les ingrédients donnés dans le coffre, et rien dans le silo ; aucune fabrication manuelle ni aucun minage n'est permis. Le premier passage doit livrer 10 de chaque ingrédient et signaler un manque de 90 : le tampon planifié est de 10 pièces.
3. **Reconstruction.** La fixture détruit le bras ; le passage logistique suivant doit le reconstruire à sa position et dans son orientation prévues. Le bras reconstruit doit de nouveau prendre dans le coffre et déposer dans le silo, sans rien collecter.
4. **Construction par le lancement.** La fixture détruit toute la cellule, supprime le registre et donne un second silo et un bras. Le chemin de lancement doit construire et adopter sa propre cellule (`rocket-start`). Faute d'ingrédients, il doit s'arrêter à l'approvisionnement sans rien insérer dans son silo.

Essais Factorio 2.0.77 headless du 1er octobre 2026, sans client connecté, à vitesse 1 :

| Essai | Graine | Résultat |
| --- | --- | --- |
| Lancement seul (premières phases) | 73110101 | `passed=true`. Cellule prête au tick 1 319, lancement du tick 1 331 au tick 5 825, compteur de 0 à 1. |
| Avec la reconstruction | 73110101, même serveur | `passed=true`. Lancement du tick 29 412 au tick 33 965, compteur de 1 à 2, bras reconstruit à sa position prévue. |
| `verify-rocket`, non-régression du silo isolé alimenté à la main | 73110101, même serveur | `passed=true`. Compteur de 2 à 3 entre les ticks 37 447 et 40 761, avec `rocket_state` passant par la fonction Lua partagée. |
| Version finale, premier essai | 73110101, même serveur | Échec. `verify-rocket` avait tout recherché (`research_all_technologies`) : le plan a gagné les structures et l'acier (exemple ci-dessus). L'amorçage des matières premières a envoyé le personnage explorer vers un minerai, puis la bande s'est arrêtée sur « A factory zone needs an observed generator network near the actor ». Depuis, la préparation fixe explicitement les recherches. |
| Version finale | 73110101, même serveur | `passed=true`. Compteur de 3 à 4 ; cellule construite par le lancement prête au tick 76 016. |
| Version finale, serveur neuf | 73110102 | `passed=true`, détaillé ci-dessous. |

Détail de l'essai sur serveur neuf :

- **Préparation et construction.** Préparation au tick 1 021, cellule prête au tick 1 346. La bande d'un emplacement a pour origine (-17 ; -12). Le silo est en (-12,5 ; -7,5), le bras en (-16,5 ; -2,5) orienté 8, le coffre en (-16,5 ; -1,5) et le poteau en (-15,5 ; -2,5). Aucune liaison n'a été nécessaire : le poteau rejoint seul celui de la source.
- **Lancement.** Les pièces sont fixées à 99 au tick 1 353, et le contrôleur tourne du tick 1 357 au tick 5 896. Le premier passage logistique (tick 1 403) met 10 de chaque ingrédient dans le coffre et signale 90 manquants de chacun. Le bras charge les 30 objets ; la dernière pièce est engagée vers le tick 3 530. La phase `rocket_ready` arrive au tick 4 618, et le compteur passe de 0 à 1, relu au tick 5 898. `products_finished` passe de 0 à 1. Le journal compte 3 insertions dans le coffre, aucune dans le silo, 39 attentes et un lancement, sans fabrication ni minage.
- **Reconstruction.** Le bras est détruit au tick 5 901. Il est reconstruit sous l'identifiant 33 à sa position et dans son orientation prévues : il prend dans le coffre et dépose dans le silo au tick 5 943, et rien n'est collecté.
- **Construction par le lancement.** La cellule est retirée au tick 5 945. Le lancement construit sa propre cellule, prête au tick 6 129, et l'adopte. Il s'arrête ensuite sur « unsupported: low-density-structure », sans aucune insertion dans son silo.

Ces essais isolent le mécanisme : recherches, objets, énergie et 99 pièces sont fournis par la fixture. Ils ne prouvent ni la fabrication des ingrédients, ni une campagne normale, et ne comptent pour aucune des trois qualifications finales.

### Limites

- Les unités de traitement et le carburant de fusée attendent des assembleurs alimentés en fluide. Le plastique attend sa chaîne fluide, que le plan du silo ne déclenche pas. D'ici là, ces ingrédients restent bruts et passent par la production pilotée, par lots d'au plus cinq cycles. Les structures et l'acier ne deviennent des cellules que si leurs recettes sont recherchées.
- Le débit d'une cellule est borné à 1,6 pièce par minute par son bras. Le vrai goulot reste les chaînes et le transport par le personnage : chaque tour d'approvisionnement est un passage logistique complet de l'usine.
- Un seul silo par usine. Le second emplacement de sa bande reste réservé et vide.
- La consommation de 3,99 MW est budgétée par l'extension de vapeur, mais n'a été vérifiée qu'avec une interface électrique injectée.
- La maintenance ne reconstruit un silo détruit que si le personnage en porte un ; sinon, le manque est signalé. Un silo remplacé pendant un lancement change d'identité et arrête ce lancement : l'objectif suivant reprend la cellule.
- Une bande, celle du silo comprise, exige un réseau alimenté en vue du personnage. L'amorçage des matières premières peut l'en éloigner, comme lors de l'échec ci-dessus.
- Aucun client graphique n'était connecté pendant ces essais.
