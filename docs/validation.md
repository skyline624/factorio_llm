# Qualification du socle — 12 septembre 2026

Ce document décrit des essais de composants et des fixtures synthétiques sur Factorio **2.0.77** de base, graine **424242**. Aucune de ces parties ne constitue une campagne autonome jusqu'à la fusée.

## Vérifications réalisées

- Compilation Release des sept projets .NET, sans avertissement.
- 93 tests hors ligne réussis : 58 pour l'adaptateur Ollama, 18 pour le transport et les reçus, 17 pour le host, dont la détection de régression, le contrôle exclusif et les transitions de défense. L'appel cloud est exclu de la suite ordinaire.
- Un appel réel à `glm-5.3-flash:cloud` via Ollama sur des faits synthétiques a produit un objectif valide : 200 plaques de fer. Une tentative, 672 tokens d'entrée et 92 de sortie, environ 1,575 seconde côté client. Il ne commande pas le jeu.
- Une fixture native démarre sans client : marche avec position réellement changée et temps écoulé, fabrication de deux engrenages consommant quatre plaques, minage de trois minerais avec durée native, refus de minage hors portée, soumission répétée sans nouvel effet, conflit d'identifiant refusé et annulation persistée.
- La fixture étendue vérifie construction d'un coffre avec consommation d'un objet, refus d'un second placement au même endroit sans perte, insertion/retrait exacts et transfert partiel limité à 90 plaques par la capacité restante du coffre. Un four produit cinq plaques à partir de cinq minerais et de combustible réellement transférés ; ses sorties sont reprises par l'acteur. Réglage de recette et rotation de tapis sont relus dans le moteur. Une recherche dont le prérequis manque est refusée sans sélection ni déblocage.
- Le tir contre un petit déchiqueteur créé dans la fixture a consommé quatre balles, y compris dans un chargeur partiellement utilisé. L'ennemi a infligé sept points de dégâts avant sa disparition ; le personnage est resté vivant. Cela vérifie une action de tir native contre un attaquant, pas une politique de défense autonome.
- Une cible placée au-delà de la zone normale du personnage a été refusée avec `target_not_visible`, sans consommation de munition.
- Une boucle de défense C# a détecté un attaquant, annulé une attente en cours, réobservé puis commandé le tir. Les dernières mesures donnent 26 ticks entre création de l'attaquant et acceptation du tir en standalone, 45 ticks avec pilote connecté. Dans chaque essai, quatre balles ont été consommées et le personnage a survécu. L'identifiant du personnage est resté le même avant/après combat et connexion/déconnexion ; un seul personnage était présent. La boucle ne dépend d'aucun appel au LLM. Ces mesures ponctuelles ne constituent pas une garantie de latence sous charge ni une qualification de défense complète.
- La mort native pendant une opération interrompt celle-ci avec `actor_dead`. Sans joueur connecté, un nouvel acteur apparaît après les 600 ticks du délai normal ; incarnation et génération changent. Les 15 plaques restent dans un seul cadavre, puis sont récupérées exactement par son identifiant observé. Un corps neutre provenant d'un autre personnage est refusé. Cette preuve porte sur le transfert des plaques ; la récupération de tout l'équipement et la mort avec pilote connecté restent à qualifier.
- Dans le client graphique connecté, le pilote est attaché au même identifiant d'entité que l'IA. Le bouton manuel incrémente le compteur d'assistance et les commandes IA sont refusées avec `manual_control`. Le retour IA permet la marche scriptée du personnage connecté. La déconnexion conserve le personnage, qui marche ensuite sans client.
- Le premier raccordement a révélé une duplication provenant du scénario freeplay. La configuration du scénario supprime désormais les kits destinés à de nouveaux joueurs et le crash tardif ; le corps temporaire vide du nouveau pilote est retiré. Le jeu affiche un seul personnage après connexion. Un ancien personnage possédant des objets reste préservé.
- Les réglages observés conservent pollution, évolution et expansion actives ; le mode pacifique est désactivé. La défense est qualifiée seulement contre l'attaquant isolé de la fixture.

Les rapports JSON/JSONL bruts restent dans `.runtime/`, avec sauvegardes, identités de session et journaux locaux. Ils ne sont pas publiés. Les coûts et positions ont été comparés à des lectures natives indépendantes des reçus du mod.

## Défauts rencontrés et corrigés

Factorio ignore une commande RCON vide. Une barrière non vide est nécessaire ; elle est envoyée après la première réponse de la commande pour éviter la perte observée d'une réponse lors de commandes enchaînées. Les tests conservent la couverture des réponses fragmentées et des caractères UTF-8 partagés entre paquets.

Sur une carte neuve, la première commande Lua a renvoyé une chaîne vide sans initialiser l'acteur. Le démarrage vérifie désormais une impression fixe, sans effet sur le personnage, au plus deux fois avant le handshake. La réponse exacte est journalisée localement. Aucune action métier n'est répétée pour contourner cette condition.

Un arrêt avant la première sauvegarde automatique a aussi révélé que `/server-save` échoue si le dossier `saves` est absent. Le host crée ce répertoire lors de la préparation du profil et avant la demande de checkpoint ; un nouvel arrêt avant autosave a réussi.

Certains bâtiments de base ont un `unit_number` mais ne sont pas accessibles par `get_entity_by_unit_number`. Le mod conserve leurs références natives lors de la construction et de l'observation. Les transferts de la fixture vérifient cette résolution.

`character.prototype.respawn_time` est exprimé en secondes : le mod le convertit désormais en ticks. Les corps de personnages sont neutres et n'ont pas de `unit_number` ; leur provenance est enregistrée depuis `on_post_entity_died`, corrélé à la mort de l'acteur. Une position ou une force neutre ne suffit pas pour autoriser une récupération.

Sans aucun joueur dans sa force, le personnage seul ne rafraîchit pas la visibilité cartographique native ; les requêtes `chart` peuvent rester en attente. La perception autorise donc explicitement les cinq secteurs sur cinq autour du secteur courant du personnage, en plus de la visibilité native courante. Cette zone a été comparée au client connecté ; après déplacement, le moteur conserve temporairement aussi des secteurs précédents. La qualification exhaustive des radars, de cette expiration et des observateurs distants reste à réaliser.

## Limites restantes

Les douze kinds annoncés par le mod sont des capacités implémentées ; seuls les mécanismes explicitement listés ci-dessus ont une preuve moteur dans cette livraison. Les cas étendus de transfert, les recettes avec restitution, la sélection d'une recherche disponible, le cycle de mort avec pilote, la récupération complète et le lancement du silo nécessitent encore leurs qualifications.

L'observation complète de l'usine, la comptabilité du transit et des segments fluides, la fuite et la défense de l'usine, les routes et implantations calculées en C#, la mémoire SQLite et la traduction des objectifs libres en progression scientifique restent à développer. Les indicateurs de couverture du protocole annoncent ces lacunes.

Le watermark local refuse une régression observée de tick, d'incarnation ou de génération et un autre monde. Il ne démontre pas une détection universelle d'une ancienne sauvegarde restaurée puis avancée au-delà du dernier tick connu. La reprise gérée des sessions reste à implémenter.

Les trois campagnes normales jusqu'à la fusée, sans assistance, restent entièrement à qualifier.
