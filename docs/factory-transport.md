# Liaisons persistantes entre cellules

`FactoryDirector` ajoute au plus deux liaisons par appel après la construction des étapes de production. C# choisit un producteur enregistré et un ingrédient solide de la recette destinataire, puis calcule les bras, les poteaux et les tapis depuis la géométrie native observée. Les ateliers conservent leurs coffres d'entrée et de sortie.

Un producteur utilise un seul bras extracteur filtré pour un article. Son bus peut être prolongé vers plusieurs consommateurs : les bras précédents gardent leur point de prise, et le tapis terminal devient un tapis intérieur. Le calcul autorise seulement les voisinages du bus qui ne créent pas une arrivée depuis un ancien tapis ; le graphe natif est vérifié après construction. Les lignes étrangères restent des obstacles.

Chaque receveur est filtré sur l'article de la liaison, en qualité normale. Un fil rouge natif le relie à son coffre d'entrée et la condition `article < maximum` limite le tampon. Le maximum vient des ingrédients et de la part de production de la cellule, avec une borne de 10 000 objets. Quand le stock disponible du produit atteint son plafond, la logistique conserve le maximum prévu mais passe temporairement la condition à `article < 0`. Elle rétablit le tampon lorsque la production doit reprendre. Les bras ne transmettent pas leur main au circuit et ne prennent pas leurs filtres depuis le réseau.

Le registre conserve la liaison, ses cellules d'origine/destination et le plan de chaque pièce avant les mutations. Les nouveaux bras de transport sont créés filtrés et désactivés par script, dans la même opération de construction. Une condition de circuit sans réseau connecté ne suffit pas à les arrêter. Les identités des coffres sont relues dans leurs cellules après reconstruction. La maintenance reconstruit tapis et bras depuis leurs plans ; la logistique remet les filtres et les fils en place avant de réactiver le bras et d'accepter à nouveau la liaison. Une extension interrompue peut adopter son ancien tapis terminal encore orienté dans l'ancienne direction, puis le tourner avec un reçu natif et une observation de confirmation.

Une photographie de toute l'usine propre connue fournit les connexions natives des tapis, les points de prise/dépôt, les filtres, les fils et l'alimentation des bras. Les nombres de voisins incluent les connexions natives même quand leur identité n'appartient pas au périmètre connu : une branche inconnue de tapis invalide la preuve. Un coffre relié à un circuit extérieur, une pièce manquante, un filtre différent, un article étranger en transit ou un réseau sans générateur invalide également la liaison.

`FactoryLogistics` laisse aux tapis les ingrédients dont la liaison est saine. Elle ne collecte pas l'intermédiaire dans le coffre source quand aucun consommateur encore desservi par l'avatar ne le demande. Les consommateurs sans liaison saine conservent les transferts par l'avatar. L'approvisionnement des autres ingrédients, du combustible et des laboratoires continue selon les priorités existantes.

Quand des consommateurs sans liaison manquent du même article, C# réserve au coffre source un lot pour l'avatar : le manque après déduction du stock porté, limité à un quart de pile native. L'extracteur lit ce coffre par son propre fil rouge et ne prend que lorsque `article > réserve`. Le tapis transporte le surplus. Cette réserve durable empêche une ligne de vider continuellement le coffre avant la collecte de la logistique. Quand le manque disparaît, le seuil revient à zéro ; le fil reste connecté pour que la condition native reste effective. La vérification de santé exige aussi le seuil, le sens de comparaison et l'absence de circuit étranger sur le coffre source.

La construction utilise la même règle de pause que la logistique : un producteur dont le stock disponible a atteint son plafond ne reçoit pas de nouvelle liaison d'entrée. La photographie de demande doit appartenir au même acteur. Les stocks déjà engagés dans les laboratoires ou les entrées des machines ne sont pas comptés comme des sorties disponibles. Les chantiers enregistrés auparavant sont terminés avant cette sélection, pour conserver les plans encore valides.

Pour les entrées encore desservies par l'avatar, un lot insuffisant remplit les tampons selon leur fraction déjà chargée et les consommations calculées par le plan. Les tampons les moins remplis montent ensemble ; les objets restants après arrondi vont au tampon dont la fraction après insertion est la plus faible. Le surplus ne remplit plus les premiers coffres selon l'ordre du registre. Pour quatre packs verts par minute, les tapis demandent vingt engrenages de tampon, les bras quarante : une fabrication de tapis en produit deux. Un lot de vingt-cinq engrenages se répartit en huit pour les tapis et dix-sept pour les bras, permettant seize packs verts si les autres ingrédients et l'alimentation sont disponibles.

Une liaison dont la source est épuisée ou dont un destinataire est encore en construction reste invalide pour l'approvisionnement automatique. La remise en configuration ne déclenche pas de déplacement vers ces extrémités inactives. La collecte des sorties encore présentes et l'approvisionnement par l'avatar restent disponibles ; le remplacement de la source du bus n'est pas encore automatique.

Cette vérification structurelle ne reprend pas le bilan d'un lot fini de la commande [`transport`](belt-transport.md). Le producteur et les consommateurs continuent à fabriquer. La preuve de production demande des sorties et des compteurs natifs constatés dans le jeu.

## Essai préparé headless

```powershell
dotnet $hostDll start --fixture --seed 20261039
dotnet $hostDll verify-factory-transport --session $sessionFile
dotnet $hostDll stop --session $sessionFile
```

Le scénario fournit explicitement énergie, recherches, équipements de construction et plaques de fer/cuivre. Aucun engrenage ni pack scientifique n'est fourni. C# construit un atelier d'engrenages et deux ateliers de science rouge, prolonge la première liaison vers le second consommateur, puis vérifie les sorties. Un passage de la logistique générale doit laisser le transport des engrenages aux tapis.

Une qualification distincte vérifie la répartition d'un lot limité :

```powershell
dotnet $hostDll start --fixture --seed 20261043
dotnet $hostDll verify-factory-logistics --session $sessionFile
dotnet $hostDll stop --session $sessionFile
```

Ce scénario fournit explicitement les recherches, l'énergie, trois assembleuses et leurs équipements, vingt-cinq engrenages, vingt-cinq circuits et cinquante plaques de fer. Les six bras fournis sont tous placés dans les cellules ; aucun tapis ni pack vert n'est fourni. C# construit les ateliers de tapis, de bras et de science verte, puis utilise deux passages de la logistique habituelle. Le rapport demande les transferts de huit et dix-sept engrenages, les compteurs natifs de huit et dix-sept fabrications intermédiaires, seize tapis et dix-sept bras dans leurs sorties, puis seize packs verts réellement fabriqués. Il vérifie aussi le même personnage, aucun pilote connecté et aucune fabrication ou extraction manuelle. Cette préparation ne prouve pas la fabrication autonome des engrenages ou des circuits.

Le scénario de graine **20261043** réussit dans Factorio **2.0.77 headless** : les sorties de seize tapis et dix-sept bras sont constatées au tick **6218**, puis seize packs verts dans le coffre au tick **18304**. Le compteur de production de la force confirme les seize packs au tick **18306**. Le monde est sauvegardé et arrêté. Les **1 202 tests hors ligne** passent : 1 057 pour l'agent, 108 pour les transports de modèles et 37 pour l'infrastructure ; un test cloud facultatif est ignoré.

Le scénario suspend ensuite explicitement les deux machines destinataires pour constater la limite de leurs coffres, détruit un tapis et un receveur pour qualifier la maintenance, puis vérifie la condition de pause sur demande. Ces manipulations sont des fautes préparées de fixture ; elles ne sont jamais exécutées dans une partie normale. Le rapport privé distingue ces preuves d'une campagne autonome.

Une phase supplémentaire fournit explicitement le kit et les plaques d'un atelier de tapis sans liaison d'engrenages. La logistique doit lui transmettre des engrenages produits par l'atelier existant, maintenir le graphe du bus, constater des tapis fabriqués dans le jeu puis libérer la réserve. Aucun engrenage ni tapis de sortie n'est fourni dans cette phase. La preuve précédente sans transfert d'engrenages par l'avatar porte sur les deux consommateurs reliés ; la phase de partage vérifie séparément le consommateur sans liaison.

Le 2 octobre 2026, la graine **20261039** passe dans Factorio **2.0.77 headless**. Les 64 tapis et trois bras du bus sont calculés et enregistrés par C#. L'extension conserve les identités de toutes les pièces précédentes, y compris l'extracteur. Au tick **13110**, les deux coffres de sortie contiennent respectivement dix et cinq packs rouges. Le passage de la logistique collecte ces quinze packs, avec zéro collecte, insertion ou manque d'engrenages.

Après suspension explicite des deux machines, les coffres d'entrée se stabilisent à **trois engrenages chacun**. La maintenance reconstruit deux pièces détruites et la vérification du graphe, des filtres, du câblage et de l'alimentation passe au tick **16012**. Les coffres restent dans la limite de trois objets, avec une tolérance d'un objet en main au déclenchement du seuil. La pause sur demande est relue au tick **16808**. Les statistiques natives finales comptent **73 engrenages et 15 packs rouges produits** au tick **16815**, depuis un compteur initial nul. Les journaux ne contiennent aucun minage, fabrication manuelle ou transfert d'engrenages par l'avatar. Le personnage est le même, sans joueur connecté ; le monde est sauvegardé et arrêté.

Les essais préparés précédents ont corrigé un champ d'observation des coffres, une recherche scientifique manquante dans la préparation et une recherche d'entité exigeant un drapeau de prototype absent. La graine 20261037 avait passé le scénario initial ; la graine 20261038 a ensuite révélé le dépassement du tampon pendant la reconstruction d'un bras non câblé. La désactivation native dans l'opération de construction corrige ce cas dans la graine 20261039. Tous ces mondes de fixture sont sauvegardés et arrêtés.

Les **1 186 tests hors ligne** passent : 1 041 pour l'agent, 108 pour les transports de modèles et 37 pour l'infrastructure ; un test cloud facultatif est ignoré. Le scénario préparé ne qualifie ni une campagne normale ni un lancement de fusée.

Les graines **20261040 et 20261041** qualifient ensuite le partage de la source dans Factorio **2.0.77 headless**. Dans chaque scénario, la logistique transmet cinq engrenages produits par l'atelier existant au consommateur sans liaison ; son coffre reçoit dix tapis fabriqués. La réserve native vaut cinq puis revient à zéro. Dans la graine 20261041, le partage est constaté au tick **20848**, la libération au tick **21446**. Une connexion étrangère préparée est rejetée avec `foreign_circuit` au tick **21465**, sans modifier le contrôle précédent. La reconstruction de l'extracteur détruit conserve la réserve de cinq et le graphe valide au tick **22739**. Les deux mondes sont sauvegardés et arrêtés. Les **1 199 tests hors ligne** passent alors : 1 054 pour l'agent, 108 pour les transports de modèles et 37 pour l'infrastructure ; le test cloud facultatif reste ignoré.

## Progression normale constatée

Le 2 octobre 2026, le monde de développement de graine **20261019**, avec ennemis actifs et `gpt-6.1-sol` via `codex-chatgpt`, produit ses packs verts dans les cellules persistantes. Au tick **859372**, les compteurs natifs des assembleuses constatent **77 fabrications de tapis**, **99 fabrications de bras** et **60 packs verts**, depuis des compteurs initialement nuls. Au tick **863077**, `research_state` confirme **`automation-2` recherchée** et quarante packs verts consommés par les laboratoires. Aucun pack ni recherche n'est injecté dans ce monde ; aucun pilote n'est connecté. Le monde est conservé à travers les mises à jour de développement. Cette preuve de production et de recherche n'est pas une campagne finale sans assistance ni une preuve de fusée.

## Limites

La planification demande des extrémités dans la même observation locale : distance maximale de 64 unités, 200 tapis au plus par bus, 12 000 expansions par recherche, huit couples envisagés par appel. Les futurs emplacements des bandes, les rangées de ressources et la croissance électrique restent réservés. Une absence de route conserve les transferts existants ; elle ne justifie pas une position devinée. Les limites de tampon sont fixées lors de la création de la liaison ; leur redimensionnement après une hausse de la demande reste à couvrir.

Les tapis souterrains, répartiteurs, équilibrage de débit entre consommateurs et routes entre installations éloignées restent à implémenter. Des détours autour des bandes réservées peuvent être longs. La maintenance remet une liaison enregistrée en service mais ne prouve pas encore sa résistance sous attaque réelle. Les liaisons locales réduisent les transports manuels sans qualifier une usine entièrement optimisée ni un lancement autonome.
