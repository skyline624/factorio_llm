# Chimie du pétrole persistante

Les packs bleus demandent du plastique et du soufre, donc du pétrole brut, une raffinerie et des usines chimiques qui tournent en continu. Les contrôleurs historiques (`produce-fluid`, `assemble`) produisent des lots finis pilotés par le personnage. Ce module transforme la même chaîne en **cellules persistantes** du registre d'usine : elles produisent entre les décisions du modèle, la logistique réapprovisionne leurs solides et la maintenance reconstruit leurs pièces détruites.

## Planification

`FluidChainPlanner` (Core) enchaîne les recettes activées qui déplacent des fluides, à partir des quantités natives :

- une étape exige un fournisseur pour chaque ingrédient fluide : un gisement (pétrole brut), le terrain (eau) ou une étape antérieure de la chaîne. Une étape à sortie fluide qui consomme des solides, comme l'acide sulfurique, est alimentée par des services logistiques bornés et doit présenter un stock natif positif avant la construction de son consommateur. Les sorties de cellules existantes sont attendues et collectées ; seuls les solides externes manquants passent par l'approvisionnement habituel. L'ancrage stratégique accepte ainsi les batteries et les processeurs quand leurs recherches et machines sont disponibles ;
- seules les machines dont la recette est activée, ou que le personnage porte déjà, servent une chaîne (`FluidChainDirector.Machines`). Le catalogue exporte aussi les prototypes non recherchés : sans ce filtre, le béton choisissait l'assembleur 3 non débloqué au lieu de l'assembleur 2 ;
- les fluides du terrain viennent du catalogue natif : le mod exporte les fluides portés par les prototypes de tuiles (`terrainFluids`). Une recette de vidage de baril ne fait donc pas de l'eau un produit à enchaîner ; ce défaut a été observé lors du premier essai du soufre ;
- une recette dont un fluide ne peut être fourni n'est pas retenue : le craquage attend l'huile légère du raffinage avancé, à plusieurs produits, non encore pris en charge ;
- les solides (charbon du plastique, fer de l'acide) restent des matières premières livrées par `FactoryLogistics` ;
- le nombre de machines vient de la vitesse native de fabrication, bornée comme pour les assembleurs par le débit mesuré d'un bras simple (`AutomationPlanner.InserterItemsPerSecond`) quand la recette déplace des solides ;
- le débit d'une foreuse à fluide vient du gisement : vitesse de minage × quantité par cycle × montant du gisement / montant normal, divisé par le temps de minage. Le mod exporte `infinite_resource` et `normal_resource_amount`. Un chevalet sur 300 000 unités donne 600 unités de brut par minute, sur 600 000 unités 1 200.

`FactoryDirector.AutomateAsync(item, rate)` inscrit les cibles d'objets dans un plan commun d'assembleurs, de fours, de silo et de chimie. Les recettes solides qui consomment du plastique déclenchent donc aussi sa chaîne ; les processeurs déclenchent leurs circuits, câbles et acide quand ces recettes sont débloquées. Les demandes partagées s'additionnent, avec des tampons calculés par la même logistique. `FluidChainDirector` construit chaque étape fluide de ce plan. Les objectifs portant directement sur un fluide gardent son API dédiée ; les objectifs stratégiques de fluides restent en `fluid_units`.

## Géométrie calculée en C#

`FluidCellPlanner` (Core) synthétise chaque cellule à partir des boîtes fluides natives, sans gabarit :

- les ports d'un prototype sont projetés pour chaque orientation comme le moteur les tourne ;
- le poteau, et pour une recette à solides le bras d'entrée, le bras de sortie et leurs coffres, se placent sur une face de la machine qui ne porte aucune case cible de port. Le poteau, entre les deux bras, alimente les bras et la machine ; les directions des bras viennent des vecteurs natifs de prise et de dépose ;
- le moteur n'attribue les fluides de la recette aux boîtes qu'après construction. Un emplacement n'est retenu que si **chaque** attribution possible des fluides aux boîtes d'entrée compatibles se raccorde, en essayant les ordres de raccordement ; les boîtes non attribuées sont évitées comme des ports étrangers ;
- la machine et ses pièces restent hors des gisements, sur des cases libres ; les coffres doivent rester accessibles une fois les tuyaux prévus posés ;
- parmi les six premiers emplacements valides autour de l'ancrage, celui qui demande le moins de tuyaux l'emporte. Lors du premier essai, la raffinerie la plus proche demandait 18 tuyaux pour contourner le chevalet ; la même préparation en demande désormais 4 ;
- une foreuse à fluide se place sur un gisement libre, orientée pour que la case devant son port reste libre et hors d'un autre gisement, avec son poteau sur une autre face.

Le choix vérifie aussi une sortie du personnage après projection de toute la cellule et des tuyaux. Pour cette vérification, les tuyaux à construire réservent toute leur case : leurs boîtes de collision changent avec leurs connexions. Le contrôle de chaque pièce conserve une sortie sur une zone plus large autour du personnage, au lieu de considérer un simple dégagement de quatre cases comme suffisant.

### Sol réservé

`FactoryGround` rassemble le sol que l'usine garde pour sa croissance : chaque bande avec ses emplacements non construits, chaque rangée de ressources planifiée avec son allée, et les prochaines unités de l'installation à vapeur, prévues comme l'extension les prévoit pour les chaudières en vue. Ces boîtes bloquent les bâtiments mais pas le personnage, qui marche dessus. Le chevalet et son poteau, la machine et ses pièces, ses routes de tuyaux, une nouvelle pompe et les liaisons électriques s'en tiennent écartés : un gisement sous une bande est laissé pour un gisement libre, un site de raffinerie ne prend plus la place des prochaines chaudières. Les liaisons renoncent à la croissance de la vapeur avant de renoncer à la liaison elle-même, comme celles des bandes ; les bandes et les rangées restent toujours réservées.

## Construction et registre

`FluidCellBuilder` enregistre deux sortes de cellules en zone 0, hors des bandes :

| Type | Rôles | `recipe` |
| --- | --- | --- |
| `extractor` | `drill`, `pole`, `link-n` | fluide extrait (`crude-oil`) |
| `fluid` | `machine`, `pole`, éventuellement `input-inserter`, `input-chest`, `output-inserter`, `output-chest`, `pump`, puis `pipe-n` et `link-n` | recette configurée |

Chaque rôle garde son objet, sa position et son orientation dans `plan`. `FactoryMaintenance` reconstruit donc une pièce détruite, tuyau ou pompe compris, et reconfigure la recette d'une machine reconstruite. Un chevalet porte le rôle `drill`, comme une mine, pour ne jamais recevoir de recette.

Ordre de construction :

1. **Extracteur** : recherche d'un gisement dans la zone observée, puis vers les gisements mémorisés ; objets portés d'abord ; poteau et chevalet posés ; poteau raccordé au réseau alimenté, où qu'il soit, par `CellPowerLinker` (poteaux `link-n` de la cellule). La cellule n'est prête qu'après un stock natif de brut dans le chevalet.
2. **Machine** : un extracteur alimente exactement une machine. Une nouvelle raffinerie prend un extracteur libre, dont le port ne débite encore dans rien, comme source imposée ; les autres fluides viennent du stock natif le plus proche. La construction attend d'abord un stock natif du fluide, par exemple le premier cycle de gaz de la raffinerie. Pour un fluide du terrain qu'aucune pompe observée ne fournit, l'ancrage passe à la rive la plus proche et l'emplacement doit laisser une pompe se raccorder. Machine, poteau, bras et coffres sont posés ; la recette est configurée ; les tuyaux sont calculés sur les raccords réellement observés et posés par `PipeConnectionController` ; une nouvelle pompe passe par `OffshoreSupplyController`. Les liaisons électriques viennent en dernier, pour contourner les tuyaux.

Une nouvelle cellule n'est enregistrée que si la photographie de l'usine connaît un poteau alimenté d'où la relier. Les tuyaux d'une route sont enregistrés depuis cette même photographie, qui liste toutes les entités connues, et non depuis une capture de 48 cases autour du personnage resté au bout de la route : une route longue est enregistrée en entier.

### Alimentation depuis un réseau lointain

Sur une carte 2.0 normale, le pétrole n'apparaît jamais dans la zone de départ. `CellPowerLinker` relie donc le poteau d'une cellule au réseau alimenté où qu'il se trouve, en l'étendant depuis ce réseau, comme `PowerGridController` pour une machine isolée :

- si aucun poteau alimenté n'est en vue, le personnage marche jusqu'au poteau alimenté connu le plus proche de la cellule ; la photographie de l'usine, indépendante de la vue, dit lesquels partagent un réseau avec une source ;
- chaque étape planifie une seule liaison sur la vue de 48 cases autour du personnage, vers la boîte du poteau de la cellule calculée depuis sa position prévue, même hors de vue. Le poteau est construit, enregistré aussitôt comme rôle `link-n` avec son plan, puis le personnage s'y rend ; la liaison doit elle-même être alimentée avant que la suivante soit planifiée ;
- une cellule dont le poteau a disparu de la photographie, une liaison qui ne rejoint pas le réseau, une recherche sans chemin observé ou un budget de 128 étapes épuisé arrêtent la liaison sans réessai aveugle.

### Reprise et abandon

Une cellule fluide interrompue reste `building` et compte ses tentatives (`attempts`), comme une cellule de ressource. À la reprise, la tentative est enregistrée et le personnage retourne au chevalet ou à la machine avant toute capture locale. Les pièces enregistrées qui ne figurent plus dans la photographie de l'usine sont oubliées, puis chaque rôle du plan sans entité est reconstruit à sa position prévue : pièces de la cellule d'abord, puis liaisons, tuyaux et pompe, par nom et par numéro. Une entité déjà posée à cet endroit avant l'interruption est adoptée. Le raccordement fluide retourne lui aussi à la machine quand elle est hors de vue. Après trois tentatives (`ResourceCellBuilder.MaximumAttempts`), la cellule est abandonnée sur place (`abandoned`) : ses entités restent réservées à l'usine, elle n'est plus ni servie ni reprise, et l'appel suivant planifie une nouvelle cellule.

`FluidChainDirector` construit les étapes fournisseurs d'abord. Une étape consommant un fluide extrait avance par paires extracteur–machine, jusqu'à couvrir à la fois le nombre de machines prévu et le débit natif mesuré des extracteurs. Au plus huit machines sont ajoutées par étape et par appel ; un manque restant est journalisé (`fluid-chain-stage-short`).

`FactoryLogistics` remplit le coffre d'entrée d'une cellule selon sa recette (le charbon du plastique) et vide son coffre de sortie. La cellule de soufre n'a qu'un coffre de sortie. Le plastique consomme du charbon en continu : les coffres de recette ne reçoivent que le charbon que laissent l'énergie et les brûleurs. La réserve (`FactoryLogistics.FuelReserve`) porte chaque coffre d'alimentation et sa chaudière ensemble au quart de pile, et chaque brûleur alimenté à la main à son seuil ; une chaudière affamée n'est plus privée de charbon au profit d'une cellule de plastique.

```text
automate --session FILE --item plastic-bar --quantity 12
verify-oil-chemistry --session FILE [--item plastic-bar|sulfur]
```

## Essais natifs préparés

`verify-oil-chemistry` exige une session fixture marquée. La préparation vide la zone, pose une rive d'eau à l'est, un gisement de pétrole de 600 000 unités en (12, 6) et, loin à l'ouest, une interface électrique en (-84, 0) avec son poteau en (-82,5 ; 0,5), à 94,7 cases du gisement. Elle accorde explicitement `steam-power`, `electronics`, `automation`, `oil-gathering`, `oil-processing`, `plastics` et `sulfur-processing`. Elle fournit un chevalet, une raffinerie, une usine chimique, une pompe, 150 tuyaux, 4 bras, 4 coffres, 60 poteaux et 100 charbons. Aucun pétrole, gaz, plastique ni soufre n'est injecté. Les statistiques natives de la force sont lues avant et après : seul leur accroissement compte.

Le personnage commence à côté de l'interface : sa photographie de l'usine fait connaître ce réseau, comme s'il l'avait construit. L'essai exige que le seul poteau alimenté connu soit celui de la fixture, à plus de 60 cases du gisement, donc hors de toute capture de 48 cases autour du chevalet, puis le personnage marche jusqu'au pétrole. Le registre ne contient qu'une bande planifiée, encore vide, de (-52, -8) à (-28, 8), en travers de la ligne droite entre la source et le gisement.

L'essai automatise 12 objets par minute. Il exige que l'extracteur soit alimenté par ses propres poteaux `link-n` et qu'aucune position prévue d'une cellule ne tombe sur la bande. Il sert la logistique, attend 3 600 ticks, puis sert de nouveau. Il détruit ensuite le premier tuyau de la cellule chimique par commande de fixture, vérifie sa reconstruction au service suivant, attend 1 800 ticks et exige encore une collecte. Enfin, la fixture rouvre la cellule chimique comme si sa construction avait été interrompue (`building`, une tentative), détruit son dernier tuyau et ramène le personnage près de la source ; la reprise directe de `BuildMachineAsync` doit revenir à la cellule depuis plus de 48 cases, reconstruire ce tuyau à sa position prévue et la rendre prête à sa deuxième tentative.

Essais Factorio 2.0.77 headless du 30 septembre 2026, première version, source à 32 cases du gisement, sans client connecté :

| Essai | Graine | Résultat |
| --- | --- | --- |
| Plastique, première version | 73120931 | `passed=true`. Extracteur prêt au tick 1 904 (4 liaisons depuis l'interface), raffinerie au tick 3 272 (18 tuyaux de brut), cellule de plastique au tick 4 186 (3 tuyaux de gaz). 40 charbons livrés par la logistique, puis 50 plastiques collectés 60 s plus tard. Tuyau 48 détruit, reconstruit sous l'identifiant 51, puis 27 plastiques collectés. Statistiques : 2 717 unités de brut extraites et 2 300 consommées, 990 unités de gaz produites en 22 cycles, 86 plastiques en 43 cycles, 43 charbons consommés, 83 charbons insérés dans le coffre d'entrée. Aucune fabrication manuelle, aucun minage. |
| Soufre | 73120932 | `passed=true`, après l'export des fluides du terrain. Raffinerie tournée vers le chevalet : 4 tuyaux de brut. Usine de soufre ancrée sur la rive, pompe 46 construite, 4 tuyaux d'eau et 17 de gaz. 50 soufres collectés après 60 s ; tuyau d'eau 47 détruit et reconstruit sous l'identifiant 69, puis 18 soufres collectés. Statistiques : 3 196 unités de brut extraites, 1 080 de gaz produites en 24 cycles, 1 050 d'eau et 1 050 de gaz consommées, 68 soufres en 34 cycles. Aucune fabrication manuelle, aucun minage. |
| Plastique, version finale | 73120932 | `passed=true`, même serveur après l'essai du soufre, zone vidée et registre supprimé. Chaîne prête en 2 640 ticks après la préparation : extracteur au tick 16 156, raffinerie au tick 16 849 (4 tuyaux de brut), cellule de plastique au tick 18 063 (6 tuyaux de gaz). 40 charbons livrés, 50 plastiques collectés après 60 s ; tuyau 91 détruit, reconstruit sous l'identifiant 97, puis 27 plastiques collectés. Accroissements : 2 577 unités de brut extraites et 2 400 consommées, 1 035 unités de gaz en 23 cycles, 86 plastiques en 43 cycles, 43 charbons consommés, 83 charbons insérés dans le coffre d'entrée. Aucune fabrication manuelle, aucun minage. |

Essais Factorio 2.0.77 headless du 1er octobre 2026, après les corrections de revue, avec la préparation décrite plus haut (source à 94,7 cases, bande planifiée, reprise), sans client connecté, sur un serveur neuf :

| Essai | Graine | Résultat |
| --- | --- | --- |
| Plastique | 73105002 | `passed=true`. Le personnage part de la source, marche jusqu'au pétrole, pose le chevalet, puis retourne à la source pour étendre la ligne vers lui : extracteur prêt au tick 5 269 avec 13 liaisons, de (-75,5 ; 2,5) à (8,5 ; 5,5), qui longent la bande par le sud (y = 8,5, la bande s'arrête à y = 8) ; chevalet alimenté, aucune position prévue sur la bande. Raffinerie au tick 6 152 (4 tuyaux de brut, 1 liaison), cellule de plastique au tick 7 226 (6 tuyaux de gaz). 40 charbons livrés, 50 plastiques collectés après 60 s ; tuyau 60 détruit, reconstruit sous l'identifiant 66, puis 27 plastiques collectés. Reprise : `pipe-5` (65) détruit pendant l'interruption, personnage à 94,2 cases ; retour, tuyau reconstruit sous l'identifiant 67 à sa position prévue (16,5 ; 12,5), cellule prête à sa deuxième tentative au tick 14 691. Accroissements : 3 156 unités de brut extraites et 2 900 consommées, 1 260 unités de gaz en 28 cycles, 90 plastiques en 45 cycles, 46 charbons consommés, 83 charbons insérés dans le coffre d'entrée. Aucune fabrication manuelle, aucun minage. |
| Soufre | 73105002 | `passed=true`, même serveur après le plastique, zone vidée et registre remplacé. Extracteur prêt au tick 18 909 par les mêmes 13 liaisons autour de la bande, raffinerie au tick 19 816 (4 tuyaux de brut), usine de soufre ancrée sur la rive au tick 22 878 : pompe 96 construite, 4 tuyaux d'eau et 17 de gaz, 1 liaison. 50 soufres collectés après 60 s ; tuyau 100 détruit, reconstruit sous l'identifiant 119, puis 18 soufres collectés. Reprise : `pipe-9` (106) détruit, personnage à 104,4 cases ; tuyau reconstruit sous l'identifiant 120 à (19,5 ; 9,5), cellule prête à sa deuxième tentative au tick 30 593. Accroissements : 3 914 unités de brut extraites et 3 200 consommées, 1 395 unités de gaz en 31 cycles, 1 110 d'eau et 1 110 de gaz consommées, 72 soufres en 36 cycles. Aucune fabrication manuelle, aucun minage. |

Sur le serveur de mise au point (graine 73105001), les mêmes essais ont aussi réussi : le plastique avant l'ajout de la reprise, puis le soufre et le plastique avec elle. Une première exécution du soufre s'y était arrêtée pendant l'attente de 1 800 ticks, après la chaîne, la logistique et la reconstruction du tuyau : Windows a refusé l'écriture d'un fichier d'exécution (« ressources système insuffisantes »), la mémoire engagée de la machine étant presque épuisée par d'autres charges. Relancée sans changement, elle a réussi.

Ces fixtures isolent le mécanisme. Recherches, objets, gisement et énergie sont fournis, et la cellule reprise est rouverte par la fixture : elles ne prouvent ni le déblocage autonome du pétrole ni une campagne normale, et ne comptent pour aucune des trois qualifications finales.

### Consommateurs d'acide sulfurique

`verify-fluid-consumer --session FILE --item battery|processing-unit` exige une fixture neuve. Elle reprend la préparation de pétrole et d'énergie distante, accorde les recherches nécessaires et fournit les équipements, des plaques, 200 circuits électroniques et 25 circuits avancés. La variante processeur fournit maintenant 1 000 plaques de chaque métal et dix assembleuses de niveau 2 au total, pour que les recherches activées et le plan commun puissent construire les câbles et circuits compatibles ; la variante batterie garde 50 plaques de fer et 25 de cuivre. Aucun soufre, acide, batterie ou processeur n'est fourni. Le contrôleur doit construire les étapes raffinage → soufre → acide → consommateur, amorcer l'acide par la logistique puis collecter le produit. Les quantités viennent des statistiques natives de production et de consommation, avec un départ nul ; tout minage ou fabrication manuelle refuse l'essai.

`--item processing-unit --from-materials` active explicitement aussi la recette des circuits avancés et retire les circuits électroniques et avancés fournis. Il reste les équipements, 1 000 plaques de chaque métal et 100 charbons ; le câble, le plastique, les circuits, le soufre, l'acide et le processeur doivent être fabriqués par les cellules. Le rapport vérifie séparément les statistiques natives de chacun de ces intermédiaires. La première exécution de cette composition, graine 20261024, a construit toute la chaîne mais a échoué sur la navigation depuis une poche formée par les machines et tuyaux. Cette défaite du test est conservée et a motivé le contrôle de sortie décrit plus haut ; elle ne prouve pas la réussite de cette chaîne.

Après la correction, l'essai neuf de graine **20261025** réussit en headless le 2 octobre 2026. Le directeur a construit quatre cellules de câbles, quatre de circuits électroniques, une de circuits avancés, un extracteur de brut, une raffinerie et les cellules de plastique, soufre, acide et processeurs. Au tick **91 459**, les statistiques natives, toutes nulles au départ, comptent **240 câbles, 58 circuits électroniques, 4 circuits avancés, 60 plastiques, 740 soufres, 200 unités d'acide et 1 processeur**. Le processeur est collecté et a consommé **5 unités d'acide**. La liaison finale est directement adjacente, sans tuyau supplémentaire. Aucun minage ni fabrication manuelle ; aucun pilote connecté. Le débit demandé de deux processeurs par minute n'est pas qualifié par cette première production après construction : les voyages du personnage et les limites des cellules restent à mesurer.

Rapport privé : `.runtime/fixture-20261002-174824-84a05879/fluid-consumer-qualification-c7ecc7d074fd482e855f03c32768dee8.json`. Serveur sauvegardé et arrêté. La suite ordinaire passe **1 142 tests**, avec le seul contrat cloud optionnel ignoré. Cette preuve couvre une chaîne préparée, pas une progression normale jusqu'à la fusée ; aucun client graphique n'était connecté.

Factorio 2.0.77 headless du 2 octobre 2026, aucun client connecté :

| Produit | Graine | Preuve native |
| --- | --- | --- |
| Batterie | 20261022 | `passed=true` : 250 unités d'acide produites, 100 consommées, 5 batteries produites et collectées au tick 14747. |
| Processeur | 20261023 | `passed=true` : 200 unités d'acide produites, 10 consommées, 1 processeur produit et collecté au tick 15126. La consommation peut inclure une fabrication encore engagée. |

Les ports du producteur et du consommateur sont directement adjacents dans les deux implantations calculées : zéro tuyau supplémentaire pour cette liaison, connexion native observée et consommation effective. Le réseau de pétrole et d'eau comporte les tuyaux enregistrés des étapes antérieures. Les essais n'ont fait aucun minage ni fabrication manuelle. Rapports privés : `.runtime/fixture-20261002-165828-c5945f99/fluid-consumer-qualification-4bed1a4ccd7e41a59d1cd4b7f9a91047.json` et `.runtime/fixture-20261002-170003-7ffdc01f/fluid-consumer-qualification-6615a4d572594cc89588ea006460fb1b.json`. Serveurs sauvegardés et arrêtés ; les 1 131 tests ordinaires passent, le test cloud optionnel reste ignoré.

Les échecs de préparation initiaux sont conservés dans `.runtime/fixture-20261002-165158-a287ca02/` : marqueur trop long, ancien nom de recherche, puis assertion exigeant à tort un tuyau quand les ports sont adjacents. Ces preuves restent des fixtures ; elles ne démontrent pas la production autonome des matériaux injectés ni un débit soutenu en campagne normale.

## Limites

- Portée de la liaison électrique : 128 étapes, soit un peu moins de 128 petits poteaux (environ 900 cases), planifiées chacune sur le sol observé à 48 cases autour du personnage. Une étendue d'eau plus large que la portée des fils, ou un obstacle sans détour visible, arrête la liaison (`NoObservedPath`) sans exploration. Le poteau de départ doit figurer dans la photographie de l'usine, donc avoir été construit ou vu par le personnage.
- La réparation de l'alimentation des cellules silencieuses (`FactoryCellBuilder.RepairPowerAsync`) ne visite que les cellules de bande. Une liaison de cellule fluide qui part d'un poteau non enregistré, par exemple un poteau de l'installation à vapeur ou de l'extension posée pour la recherche du pétrole, n'est pas réparée si ce poteau est détruit : la maintenance ne reconstruit que les rôles enregistrés et journalise `factory-power-fault`.
- Une cellule abandonnée reste en place, n'est pas démontée et garde son gisement ; ses entités restent exclues de la production pilotée. Une reconstruction refusée pendant une reprise consomme une tentative.
- La croissance de la vapeur n'est réservée que pour les chaudières en vue lors de chaque planification ; les tuyaux et la pompe gardent le sol lu autour de la machine.
- La pompe prévue n'est pas projetée pendant le choix de l'emplacement : les routes des autres fluides et l'accès aux coffres peuvent traverser sa case, et `PrepareJointSourceAsync` peut alors échouer après la pose de la machine.
- Le choix de l'emplacement ne vérifie que les ports d'entrée : un arbre devant le seul port de sortie de gaz d'une raffinerie bloque l'étape suivante, faute de minage des obstacles.
- Les objectifs d'objets cumulent maintenant leurs étapes de raffinage. Les appels directs portant sur un fluide conservent leur plan dédié. La puissance des chevalets construits par paires n'est pas budgétée avant leur pose.
- Un extracteur n'alimente qu'une machine. Si un chevalet ne couvre pas une raffinerie, la chaîne ajoute des paires au lieu de rejoindre un réseau de brut commun.
- La recherche d'un gisement se limite à la zone observée et aux gisements mémorisés ; elle n'explore pas. Les arbres et rochers restent des obstacles : ni l'emplacement ni les tuyaux ne les minent.
- Les tuyaux se limitent à la zone observée autour du personnage (48 cases) et à 200 tuyaux par route. Une route interrompue à mi-chemin, dont les tuyaux ne sont enregistrés qu'à la fin, n'est pas réparée automatiquement : la reprise ne reconstruit que les rôles enregistrés.
- La nouvelle pompe doit tenir contre la machine : une usine alimentée en eau se place donc sur la rive, loin de la raffinerie si besoin.
- Le raffinage avancé, le craquage et le lubrifiant ne sont pas encore construits en cellules. L'acide vers les batteries et processeurs est couvert par les fixtures ci-dessus ; le béton alimenté en eau est planifiable mais n'a pas encore sa qualification native.
- Une recette verrouillée ou sans fournisseur compatible reste une matière première explicite du plan. Le plan ne débloque aucune recherche et ne garantit pas que les limites de huit cellules par étape et les voyages du personnage permettent d'atteindre le débit demandé.
- Les plafonds de stocks interrompent le réapprovisionnement des solides. Ils n'arrêtent pas encore une cellule alimentée uniquement en fluides : les 740 soufres de l'essai de composition montrent cette surproduction. La régulation de ces entrées et les transports permanents entre cellules restent nécessaires pour l'optimisation de la chaîne.
- L'observation `observe` limitée à 200 entités connues, utilisée par la production pilotée, finit par être dépassée par une usine riche en tuyaux.
- Aucun client graphique n'était connecté pendant ces essais.
