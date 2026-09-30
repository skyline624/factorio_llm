# Chimie du pétrole persistante

Les packs bleus demandent du plastique et du soufre, donc du pétrole brut, une raffinerie et des usines chimiques qui tournent en continu. Les contrôleurs historiques (`produce-fluid`, `assemble`) produisent des lots finis pilotés par le personnage. Ce module transforme la même chaîne en **cellules persistantes** du registre d'usine : elles produisent entre les décisions du modèle, la logistique réapprovisionne leurs solides et la maintenance reconstruit leurs pièces détruites.

## Planification

`FluidChainPlanner` (Core) enchaîne les recettes activées qui déplacent des fluides, à partir des quantités natives :

- une étape chimique (plastique, soufre, acide sulfurique) exige un fournisseur pour chaque ingrédient fluide : une recette de la chaîne (traitement basique du pétrole pour le gaz), un gisement (pétrole brut) ou le terrain (eau) ;
- les fluides du terrain viennent du catalogue natif : le mod exporte les fluides portés par les prototypes de tuiles (`terrainFluids`). Une recette de vidage de baril ne fait donc pas de l'eau un produit à enchaîner ; ce défaut a été observé lors du premier essai du soufre ;
- une recette dont un fluide ne peut être fourni n'est pas retenue : le craquage attend l'huile légère du raffinage avancé, à plusieurs produits, non encore pris en charge ;
- les solides (charbon du plastique, fer de l'acide) restent des matières premières livrées par `FactoryLogistics` ;
- le nombre de machines vient de la vitesse native de fabrication, bornée comme pour les assembleurs par le débit mesuré d'un bras simple (`AutomationPlanner.InserterItemsPerSecond`) quand la recette déplace des solides ;
- le débit d'une foreuse à fluide vient du gisement : vitesse de minage × quantité par cycle × montant du gisement / montant normal, divisé par le temps de minage. Le mod exporte `infinite_resource` et `normal_resource_amount`. Un chevalet sur 300 000 unités donne 600 unités de brut par minute, sur 600 000 unités 1 200.

`FactoryDirector.AutomateAsync(item, rate)` délègue à `FluidChainDirector` quand aucune recette d'assembleur solide n'existe pour l'objet et qu'une chaîne fluide le fabrique. L'ancrage stratégique accepte les objectifs `items_per_minute` dont le produit est un solide d'une telle chaîne, comme `plastic-bar` et `sulfur` ; les fluides eux-mêmes restent des objectifs en `fluid_units`.

## Géométrie calculée en C#

`FluidCellPlanner` (Core) synthétise chaque cellule à partir des boîtes fluides natives, sans gabarit :

- les ports d'un prototype sont projetés pour chaque orientation comme le moteur les tourne ;
- le poteau, et pour une recette à solides le bras d'entrée, le bras de sortie et leurs coffres, se placent sur une face de la machine qui ne porte aucune case cible de port. Le poteau, entre les deux bras, alimente les bras et la machine ; les directions des bras viennent des vecteurs natifs de prise et de dépose ;
- le moteur n'attribue les fluides de la recette aux boîtes qu'après construction. Un emplacement n'est retenu que si **chaque** attribution possible des fluides aux boîtes d'entrée compatibles se raccorde, en essayant les ordres de raccordement ; les boîtes non attribuées sont évitées comme des ports étrangers ;
- la machine et ses pièces restent hors des gisements, sur des cases libres ; les coffres doivent rester accessibles une fois les tuyaux prévus posés ;
- parmi les six premiers emplacements valides autour de l'ancrage, celui qui demande le moins de tuyaux l'emporte. Lors du premier essai, la raffinerie la plus proche demandait 18 tuyaux pour contourner le chevalet ; la même préparation en demande désormais 4 ;
- une foreuse à fluide se place sur un gisement libre, orientée pour que la case devant son port reste libre et hors d'un autre gisement, avec son poteau sur une autre face.

## Construction et registre

`FluidCellBuilder` enregistre deux sortes de cellules en zone 0, hors des bandes :

| Type | Rôles | `recipe` |
| --- | --- | --- |
| `extractor` | `drill`, `pole`, `link-n` | fluide extrait (`crude-oil`) |
| `fluid` | `machine`, `pole`, éventuellement `input-inserter`, `input-chest`, `output-inserter`, `output-chest`, `pump`, puis `pipe-n` et `link-n` | recette configurée |

Chaque rôle garde son objet, sa position et son orientation dans `plan`. `FactoryMaintenance` reconstruit donc une pièce détruite, tuyau ou pompe compris, et reconfigure la recette d'une machine reconstruite. Un chevalet porte le rôle `drill`, comme une mine, pour ne jamais recevoir de recette.

Ordre de construction :

1. **Extracteur** : recherche d'un gisement dans la zone observée, puis vers les gisements mémorisés ; objets portés d'abord ; poteau et chevalet posés ; poteau raccordé au réseau alimenté par les liaisons de `FactoryCellBuilder` (poteaux `link-n` de la cellule). La cellule n'est prête qu'après un stock natif de brut dans le chevalet.
2. **Machine** : un extracteur alimente exactement une machine. Une nouvelle raffinerie prend un extracteur libre, dont le port ne débite encore dans rien, comme source imposée ; les autres fluides viennent du stock natif le plus proche. La construction attend d'abord un stock natif du fluide, par exemple le premier cycle de gaz de la raffinerie. Pour un fluide du terrain qu'aucune pompe observée ne fournit, l'ancrage passe à la rive la plus proche et l'emplacement doit laisser une pompe se raccorder. Machine, poteau, bras et coffres sont posés ; la recette est configurée ; les tuyaux sont calculés sur les raccords réellement observés et posés par `PipeConnectionController` ; une nouvelle pompe passe par `OffshoreSupplyController`. Les liaisons électriques viennent en dernier, pour contourner les tuyaux.

`FluidChainDirector` construit les étapes fournisseurs d'abord. Une étape consommant un fluide extrait avance par paires extracteur–machine, jusqu'à couvrir à la fois le nombre de machines prévu et le débit natif mesuré des extracteurs. Au plus huit machines sont ajoutées par étape et par appel ; un manque restant est journalisé (`fluid-chain-stage-short`).

`FactoryLogistics` n'a pas changé : il remplit le coffre d'entrée d'une cellule selon sa recette (le charbon du plastique) et vide son coffre de sortie. La cellule de soufre n'a qu'un coffre de sortie.

```text
automate --session FILE --item plastic-bar --quantity 12
verify-oil-chemistry --session FILE [--item plastic-bar|sulfur]
```

## Essais natifs préparés

`verify-oil-chemistry` exige une session fixture marquée. La préparation vide la zone, pose une rive d'eau à l'est, une interface électrique et son poteau à l'ouest, et un gisement de pétrole de 600 000 unités. Elle accorde explicitement `steam-power`, `electronics`, `automation`, `oil-gathering`, `oil-processing`, `plastics` et `sulfur-processing`. Elle fournit un chevalet, une raffinerie, une usine chimique, une pompe, 150 tuyaux, 4 bras, 4 coffres, 30 poteaux et 100 charbons. Aucun pétrole, gaz, plastique ni soufre n'est injecté. Les statistiques natives de la force sont lues avant et après : seul leur accroissement compte.

L'essai automatise 12 objets par minute. Il sert la logistique, attend 3 600 ticks, puis sert de nouveau. Il détruit ensuite le premier tuyau de la cellule chimique par commande de fixture, vérifie sa reconstruction au service suivant, attend 1 800 ticks et exige encore une collecte.

Essais Factorio 2.0.77 headless du 30 septembre 2026, sans client connecté :

| Essai | Graine | Résultat |
| --- | --- | --- |
| Plastique, première version | 73120931 | `passed=true`. Extracteur prêt au tick 1 904 (4 liaisons depuis l'interface), raffinerie au tick 3 272 (18 tuyaux de brut), cellule de plastique au tick 4 186 (3 tuyaux de gaz). 40 charbons livrés par la logistique, puis 50 plastiques collectés 60 s plus tard. Tuyau 48 détruit, reconstruit sous l'identifiant 51, puis 27 plastiques collectés. Statistiques : 2 717 unités de brut extraites et 2 300 consommées, 990 unités de gaz produites en 22 cycles, 86 plastiques en 43 cycles, 43 charbons consommés, 83 charbons insérés dans le coffre d'entrée. Aucune fabrication manuelle, aucun minage. |
| Soufre | 73120932 | `passed=true`, après l'export des fluides du terrain. Raffinerie tournée vers le chevalet : 4 tuyaux de brut. Usine de soufre ancrée sur la rive, pompe 46 construite, 4 tuyaux d'eau et 17 de gaz. 50 soufres collectés après 60 s ; tuyau d'eau 47 détruit et reconstruit sous l'identifiant 69, puis 18 soufres collectés. Statistiques : 3 196 unités de brut extraites, 1 080 de gaz produites en 24 cycles, 1 050 d'eau et 1 050 de gaz consommées, 68 soufres en 34 cycles. Aucune fabrication manuelle, aucun minage. |
| Plastique, version finale | 73120932 | `passed=true`, même serveur après l'essai du soufre, zone vidée et registre supprimé. Chaîne prête en 2 640 ticks après la préparation : extracteur au tick 16 156, raffinerie au tick 16 849 (4 tuyaux de brut), cellule de plastique au tick 18 063 (6 tuyaux de gaz). 40 charbons livrés, 50 plastiques collectés après 60 s ; tuyau 91 détruit, reconstruit sous l'identifiant 97, puis 27 plastiques collectés. Accroissements : 2 577 unités de brut extraites et 2 400 consommées, 1 035 unités de gaz en 23 cycles, 86 plastiques en 43 cycles, 43 charbons consommés, 83 charbons insérés dans le coffre d'entrée. Aucune fabrication manuelle, aucun minage. |

Ces fixtures isolent le mécanisme. Recherches, objets, gisement et énergie sont fournis : elles ne prouvent ni le déblocage autonome du pétrole ni une campagne normale, et ne comptent pour aucune des trois qualifications finales.

## Limites

- Un extracteur n'alimente qu'une machine. Si un chevalet ne couvre pas une raffinerie, la chaîne ajoute des paires au lieu de rejoindre un réseau de brut commun.
- La recherche d'un gisement se limite à la zone observée et aux gisements mémorisés ; elle n'explore pas. Les arbres et rochers restent des obstacles : ni l'emplacement ni les tuyaux ne les minent.
- Les tuyaux se limitent à la zone observée autour du personnage (48 cases) et à 200 tuyaux par route. Une route interrompue à mi-chemin n'est pas réparée automatiquement.
- La nouvelle pompe doit tenir contre la machine : une usine alimentée en eau se place donc sur la rive, loin de la raffinerie si besoin.
- Le raffinage avancé, le craquage, le lubrifiant et les produits fluides consommés par d'autres cellules (acide sulfurique vers les batteries) ne sont pas encore construits en cellules ; `FluidChainPlanner` planifie l'acide, mais aucune cellule consommatrice ne l'utilise.
- Une automatisation d'assembleur qui consomme du plastique, comme les circuits avancés, le traite encore comme matière première ; elle ne déclenche pas cette chaîne.
- L'observation `observe` limitée à 200 entités connues, utilisée par la production pilotée, finit par être dépassée par une usine riche en tuyaux.
- Aucun client graphique n'était connecté pendant ces essais.
