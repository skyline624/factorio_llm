# Réponse aux attaques, défense par îlot et équipement de survie

## Constats de campagne

Dans la partie normale de la graine 20261002 (30 septembre et 1er octobre 2026), des meutes ont attaqué trois fois en 75 minutes des zones de l'usine éloignées des quatre tourelles. Armé d'un pistolet et sans armure, le personnage passait de la santé maximale à la mort en sept secondes environ ; la retraite se déclenchait tôt mais les biteurs la rattrapaient. Un objectif de périmètre avait échoué avec « The factory core alone exceeds one observed perimeter » : les rangées de ressources et les bandes ne tenaient pas dans une seule observation native.

Le passage 16 (1er octobre, 04:20–04:52 UTC) a compté 14 morts en 30 minutes. Le personnage réapparaissait avec le seul pistolet de réapparition, puis partait explorer vers l'est à la recherche de pétrole brut sans armure ; face à un groupe, ses plans de « séparation » n'empêchaient pas sa santé de passer de 187 à 5 en cinq secondes. Trois morts ont eu lieu dans la base, où 3 tourelles couvraient 61 des 151 bâtiments industriels ; les objectifs de périmètre étaient refusés tant que le mur de pierre n'était pas recherché.

## Îlots d'industrie

`IndustryClusterPlanner` regroupe l'industrie propre connue en îlots qui tiennent chacun dans une observation spatiale native :

- membres : les bandes de l'usine (emplacements non construits compris), les entités des cellules enregistrées (sauf les poteaux de liaison `link-*`) et toute autre entité industrielle connue (`DefenseFactoryState.Industry`). Les défenses, poteaux, tapis et tuyaux n'en font pas partie : ils relieraient des sites éloignés en un anneau impossible à observer ;
- la photographie de l'usine ne donne pas les boîtes de collision : chaque entité est approchée par un carré de 5 × 5 cases (9 × 9 pour le silo), ce qui surestime l'emprise ;
- deux membres séparés de 20 cases au plus partagent un îlot (lien simple, paires triées par écart puis par identifiant), sauf si l'îlot dépasserait 75 cases dans une direction : 97 cases d'observation moins les marges de l'anneau (ouverture de 3, tourelle de 2, deux couches de murs, 2 cases de preuve) et une marge de centrage ;
- un îlot porte le nom de sa plus ancienne entité (`cluster-<identifiant natif>`), jamais une coordonnée.

L'objectif stratégique de périmètre (`perimeter-defense`) construit désormais un anneau par îlot, du plus proche au plus lointain, chacun planifié depuis le centre de son îlot avec la géométrie et la preuve d'accès de `PerimeterPlanner`. Un îlot dont l'anneau est refusé (observation insuffisante, aucune route prouvée) est journalisé (`perimeter-cluster-skipped`) sans arrêter les autres ; l'objectif n'échoue que si aucun îlot ne peut être protégé. Le résultat agrège les anneaux et détaille chacun (`rings`).

## Détection

`AttackDetector` transforme quatre signaux en un enregistrement par îlot attaqué :

1. entités enregistrées détruites : présentes au registre d'une cellule prête mais absentes de la photographie native, à la position de leur plan ;
2. entités propres sous leur santé maximale native : la photographie de l'usine exporte désormais `health` et `maxHealth`. Les bâtiments ne se régénèrent pas, ce signal persiste donc après l'attaque ;
3. unités ennemies vues par l'observation normale du personnage à 64 cases au plus (nids et vers sont des menaces fixes, pas des attaques) ;
4. combats du réflexe de défense : tirs et replis, gardés en mémoire du processus (256 au plus).

Chaque indice est attribué à l'îlot le plus proche à 24 cases au plus. Une mémoire évite de signaler deux fois la même perte : identifiants encore détruits, santé des entités endommagées (seule une baisse supplémentaire compte), ennemis déjà vus près de l'industrie et tick du dernier combat. L'enregistrement donne le tick, l'îlot, les nombres de détruits, d'endommagés, d'ennemis et de combats, et la direction (huit points cardinaux) des ennemis observés. Les points attaqués et les identifiants restent dans le journal privé `attack-log.json` (32 derniers enregistrements) ; chaque détection est aussi journalisée (`attack-detected`).

La détection s'exécute au début de chaque tour de maintenance, avant la reconstruction qui effacerait la trace d'une attaque survenue pendant un objectif, puis entre les objectifs. Un défaut de détection est journalisé (`attack-monitor-error`) et n'interrompt jamais la maintenance.

## Réponse entre les objectifs

`AttackResponseController` s'exécute dans le rappel de maintenance de `run-campaign`, avant la logistique et dans le journal entre objectifs ; la commande `attack-response --session FILE` exécute un tour isolé. Rien ne commence si un ennemi est visible à 32 cases du personnage : le réflexe combat d'abord (`deferred-enemies-near-actor`). La production y respecte les réservations de l'usine, comme pendant un objectif.

Après l'équipement du personnage, au plus deux îlots attaqués, les plus récents d'abord, reçoivent des nids de tourelles :

- tourelle : celle déjà possédée, sinon celle dont la recette est active ; munitions choisies par `DefenseDeploymentPlanner.ChooseAmmunition` ;
- nombre de nids : au plus quatre, et seulement ceux dont la tourelle et les dix chargeurs peuvent être fabriqués à la main depuis le stock (sac et coffres collectables), sans minage ni exploration ;
- murs : seulement si ce même stock couvre aussi douze murs par nid ; un mur non recherché ne retarde jamais les tourelles ;
- l'anneau entier de l'îlot est planifié, mais seuls les nids inachevés sont construits, ceux dont la tourelle couvre le plus de points attaqués d'abord, puis les plus proches de ces points ; tourelles avant murs, réarmement par la maintenance ;
- les nids sont des cellules `turret` et `wall` du registre : la maintenance les reconstruit et les réarme. Une cible d'automatisation des chargeurs est enregistrée pour la prochaine automatisation, sans immobiliser le personnage à construire la cellule.

Une réponse `turrets-deployed` ou `ring-complete` (anneau déjà complet, la maintenance le réarme) solde l'attaque ; `no-turret`, `no-ammunition`, `no-stock`, `skipped` ou `failed` la laissent ouverte pour trois essais au plus.

## Équipement de survie

Le catalogue natif expose maintenant les armes (portée, délai entre tirs, modificateur de dégâts, catégories de munitions), les armures (résistance physique fixe et proportionnelle, bonus d'inventaire) et les dégâts d'une cartouche. `SurvivalKitPlanner` choisit parmi les objets portés ou dont la recette est active :

- l'armure de meilleure protection physique (proportion, puis réduction fixe, puis bonus d'inventaire) : armure légère dès le départ, lourde après sa recherche ;
- l'arme à balles tirant le plus de dégâts par seconde (tirs par seconde × modificateur, puis portée) : la mitraillette (10 tirs/s, portée 18) remplace le pistolet (4 tirs/s, portée 15) une fois `military` recherchée ;
- une réserve de 20 chargeurs des munitions les plus fortes obtenables, les plus faibles servant de repli.

L'action `equip` du mod accepte le compartiment `armor`. Une armure portée est échangée et revient dans l'emplacement source du sac, jamais détruite ; l'échange est refusé s'il réduisait l'inventaire principal. Le reçu donne les inventaires avant/après et l'armure remplacée ; C# vérifie les débits, crédits et cartouches. L'arme est montée dans un emplacement libre puis chargée des cartouches portées les plus fortes, chaque opération décidée depuis une observation fraîche, jamais avec un ennemi en vue ; une réponse perdue est interrogée par identité. Le réflexe recharge aussi l'arme de plus longue portée et préfère les cartouches les plus fortes.

`SurvivalKitController` ne fabrique qu'à partir du stock fini des coffres collectables : il calcule l'arbre des recettes manuelles, collecte d'abord exactement ce qu'il consommera, puis fabrique. Le sac conserve ainsi ce que porte la tâche en cours, et l'équipement ne déclenche jamais de minage ni d'exploration. Il est invoqué avant chaque recherche d'exploration (ressources de la production, rangées de ressources, pétrole brut des déclencheurs de recherche, préparation de fonte, extraction vers coffre, alimentation vapeur), avant tout trajet au-delà de la zone locale de 24 cases, et entre les objectifs. Une vérification ne coûte qu'une observation tant que l'incarnation et l'équipement n'ont pas changé depuis moins de 18 000 ticks ; les appels imbriqués sont ignorés ; un passage dure au plus quatre minutes. Le journal contient `survival-kit-plan`, `survival-kit`, `survival-kit-unavailable` et `survival-kit-failed` ; un échec n'interrompt jamais le trajet.

Le choix d'armure essaie les améliorations disponibles par protection décroissante. Une recette active peut manquer de stock : après une pénurie constatée pour l'armure lourde, le contrôleur essaie l'armure légère si elle améliore la protection portée. Il conserve les mêmes règles de collecte et de fabrication, sans minage ni exploration pour obtenir l'équipement. Une fabrication échouée ne déclenche pas une autre tentative d'armure ; seuls les manques de stock constatés permettent ce repli.

Le 3 octobre 2026, une fixture headless distincte, sous Factorio 2.0.77 et avec la graine 20261126, a vérifié ce comportement. Elle active explicitement la recette lourde sans fournir d'acier, puis fournit une armure légère dans le sac : le contrôleur la porte et conserve ensuite cette protection. Une seconde préparation fournit 50 plaques de fer dans un coffre et aucune armure : le contrôleur collecte, fabrique et équipe une armure légère ; le relevé natif retrouve exactement 10 plaques restantes. Aucun minage, mort ou pilote connecté n'est constaté. Ces preuves de composant, accompagnées de 1 368 tests hors jeu réussis, ne prouvent ni la survie d'une campagne ni le lancement d'une fusée.

## Réflexe en infériorité numérique

Face à au moins trois ennemis visibles, un personnage encore en bonne santé qui voit une tourelle propre chargée sans être dans la moitié intérieure de sa portée rejoint d'abord cette tourelle ([repli](retreat.md)). Seuls les refuges sont essayés dans ce cas : une fuite locale ne distance pas les biteurs. La route est calculée avant toute préemption ; sans refuge atteignable, le personnage continue de combattre au lieu d'alterner annulation et tir.

## Faits du planificateur

Le contexte du modèle contient `defenseSituation` : les cinq dernières attaques (tick, îlot, détruits, endommagés, ennemis, combats, direction, réponse, nids ajoutés), au plus douze îlots avec leur nombre de bâtiments, ceux couverts par une tourelle active chargée et les nids enregistrés à proximité, ainsi que l'armure, l'arme, les munitions, les cartouches chargées et les chargeurs portés du personnage. Aucune coordonnée n'y figure.

## Qualification native du 1er octobre 2026

`verify-attack-response --session FILE` refuse une partie normale. La fixture marquée vide la zone, prépare deux îlots de deux fours et deux coffres distants de 48 cases, et ne laisse au personnage que le pistolet de réapparition chargé ; elle lui fournit 6 tourelles, 100 murs, 100 chargeurs, une armure légère, une armure lourde et une mitraillette. Trois petits biteurs créés à l'est reçoivent l'ordre natif `attack_area` sur l'îlot oriental pendant que le personnage se tient à l'îlot occidental. Dès qu'un bâtiment est endommagé, le contrôleur réel détecte l'attaque ; la fixture retire ensuite la meute pour représenter une attaque terminée, puis le contrôleur répond. Le personnage revient ensuite à l'îlot occidental par la navigation réelle et une seconde vague de quatre biteurs attaque l'îlot oriental. Une lecture RCON indépendante mesure les victimes par tourelle, les cartouches et l'équipement.

| Graine | Détection | Équipement | Réponse | Seconde vague | Rapport |
| --- | --- | --- | --- | --- | --- |
| 73106001 (fixture headless) | tick 6543 : îlot oriental, 1 four endommagé (193/200), 3 ennemis, direction E ; rien sur l'îlot occidental | armure lourde portée, mitraillette montée et chargée, 90 chargeurs portés, rien fabriqué | 4 nids, 4 tourelles prêtes, 64 murs (stock suffisant), toutes à l'est ; fin au tick 10173 | 4 biteurs tués par les tourelles, constatés morts au plus 186 ticks après leur apparition, 16 cartouches consommées, aucune cartouche du personnage tirée | `cd3ba335eafe4bffaebdb0341f4e4c7e`, `passed=true` |
| 73106001 (même fixture reprise, code final après rebasage) | tick 31818 : mêmes constats (1 four à 193/200, 3 ennemis, direction E) | mêmes équipements | 4 nids, 4 tourelles prêtes, 64 murs ; fin au tick 35636 | 4 biteurs tués par les tourelles (36015 → 36201), 16 cartouches, personnage sans tir | `c64326c2ba4149008e08d12404a1c9d1`, `passed=true` |

Sur le même serveur, les qualifications existantes ont été rejouées avec ce code : `verify-retreat` (`77f63d96fc9b44e1908f4ca37c012de1`, `passed=true` : 8 plans, 7 déplacements, contournement du mur, santé finale 88), `verify-defense-deployment` (`4bfb69cbcba2441d9056283174a2a3c4`, `passed=true` ; l'équipement de survie s'y est exécuté au premier long trajet et a constaté que les coffres ne couvraient pas sa réserve de chargeurs, sans toucher aux plaques portées pour les tourelles) et `verify-perimeter` (`146445a693a9408a88cdbf662623c268`, `passed=true` : un îlot, 8 nids, 96 murs, aucun trou de couverture, répétition reconnue complète, 6 biteurs sur 6 tués par les tourelles, reconstruction et réarmement à 100 coups, aucune fabrication ni minage). Ces trois qualifications ont été rejouées avec le module posé sur `965c300` ; le rebasage final sur `5e33e47` n'a modifié aucun fichier du mod, s'est appliqué sans conflit et seule `verify-attack-response` a été rejouée ensuite. La suite hors ligne compte 969 tests réussis (843 du host), un test cloud optionnel ignoré.

Deux premières exécutions de `verify-attack-response` ont échoué avant toute préparation : le motif de marqueur dépassait la borne native de 128 octets. Une sonde manuelle du marqueur a alors été envoyée ; le mod conservant le premier motif, cette fixture porte le motif « probe ». Le motif a été raccourci avant l'exécution réussie.

## Limites

- Ces essais sont des fixtures préparées en headless (`isAutonomousCampaign=false`), sans inférence cloud ; le client graphique connecté n'a pas été vérifié. Aucune campagne normale ne prouve encore ces modules.
- Le retrait de la première vague par la fixture représente une attaque terminée ; la réponse pendant une attaque encore en cours sur un îlot éloigné, puis le combat du personnage équipé, ne sont couverts que par les tests hors ligne.
- Le repli vers une tourelle en infériorité numérique et la préférence d'arme du réflexe ne sont prouvés que par des tests hors ligne. Sans tourelle chargée observée, un personnage en danger fuit encore localement et peut être rattrapé.
- Sans stock fini dans les coffres, ni l'équipement ni les nids ne sont fabriqués : l'attaque reste ouverte (trois essais). L'objectif de périmètre, lui, produit par le chemin ordinaire.
- Une réponse ajoute au plus quatre nids par îlot ; un anneau complet n'est renforcé que par la reconstruction et le réarmement. Ni nids plus denses, ni tourelles laser ou lance-flammes, ni estimation de la puissance ennemie.
- L'emprise des îlots est approchée par des carrés : un site très étendu peut être scindé, et l'anneau d'un îlot peut passer près d'un îlot voisin (le planificateur décale ou abandonne alors un nid).
- L'équipement ne récupère pas lui-même le contenu d'un corps et ne remplit pas les grilles d'équipement des armures modulaires ; il n'utilise que les armes à balles.
