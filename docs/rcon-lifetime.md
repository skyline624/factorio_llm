# Connexion RCON pendant les commandes longues

Deux tentatives de production ont été interrompues par une erreur d'adresse de socket déjà utilisée. Le journal système Windows signale l'épuisement des ports éphémères (événement TCP/IP 4231), puis le refus de réutiliser trop tôt un point de terminaison vers le même serveur (4227, à l'heure de la première interruption). Le transport ouvrait et fermait une connexion authentifiée pour chaque échange, y compris les observations fréquentes pendant les déplacements.

Ces événements correspondent aux indicateurs décrits par la [documentation Microsoft sur l'épuisement des ports TCP/IP](https://learn.microsoft.com/en-us/troubleshoot/windows-client/networking/tcp-ip-port-exhaustion-troubleshooting). Le nombre de connexions en attente de fermeture ne suffit pas, à lui seul, à établir le diagnostic ; ici, les erreurs système et les interruptions de connexion fournissent les preuves supplémentaires.

## Comportement corrigé

Les clients de jeu créés par une commande conservent maintenant leur connexion. Un sémaphore sérialise les échanges ; les appels concurrents ne mélangent pas leurs réponses. Chaque commande et sa barrière possèdent des identifiants distincts sur cette connexion. La barrière non vide reste envoyée seulement après la première réponse de Factorio.

Une erreur de lecture, d'écriture, d'authentification ou de protocole, un délai dépassé ou une annulation pendant un échange ferment la connexion. Le transport ne rejoue pas l'appel. Le contrôleur doit consulter les reçus et réconcilier les effets incertains ; seul un nouvel appel explicite peut ouvrir une nouvelle connexion. Annuler un appel qui attend encore son tour ne ferme pas l'échange actif.

La durée de vie est explicite : les commandes, les contrôleurs de qualification et les fonctions de reprise libèrent leur client par `await using`. Cette libération attend la fin de l'échange actif, ferme le socket et refuse les appels suivants. Les petites commandes RCON natives isolées gardent leur connexion limitée à un échange. Aucun paramètre réseau Windows n'est modifié.

## Vérifications hors ligne

La suite après les corrections de transport et de reprise de four compte 405 tests réussis et un test cloud optionnel ignoré. Cinq nouveaux cas de transport vérifient :

- trois appels successifs et trois appels concurrents avec une seule authentification et des identifiants distincts ;
- une coupure après réception d'une mutation, sans rejeu, puis une observation explicite sur une nouvelle connexion ;
- le même comportement après dépassement de délai ;
- l'annulation d'un appel en attente, la poursuite de l'échange actif, la fermeture native du socket et le refus après libération.

La compilation Release des huit projets ne rapporte aucun avertissement ni erreur. Ces tests couvrent le transport ; ils ne prouvent aucune progression autonome jusqu'à la fusée.

## Premier essai dans le jeu

Le monde normal a repris le checkpoint 1197080. Pendant les déplacements de la commande de production, un relevé du système a constaté une seule connexion établie et quatre connexions en attente de fermeture, issues des appels isolés de démarrage et de preuve. Après l'arrêt de la commande, aucune connexion établie ne restait et cinq étaient en attente de fermeture. Les nouvelles ouvertures par observation ont disparu.

Cette première commande a toutefois échoué pour une autre raison : le contrôleur avait compté les minerais du four 683, puis sélectionné le four 626 plus proche pour cuire le lot. Le transfert a été refusé au tick 1202108 avec zéro objet transféré. Deux tests reproduisent ce défaut. La reprise conserve maintenant l'identifiant du four chargé et refuse de le remplacer implicitement ; les ingrédients portés sont également relus avant toute nouvelle insertion. Cette erreur de production est distincte du problème réseau corrigé.

La tentative suivante a produit 21 nouvelles plaques mais ravitaillait encore la foreuse alors que le four contenait assez de minerai. Elle a été interrompue pendant le développement. Son dernier déplacement a été consulté directement et confirmé terminé au tick 1245032 ; aucune mutation incertaine n'a été répétée. Le contrôleur d'extraction relit désormais les ingrédients, la fabrication engagée et les sorties dans une photographie atomique avant de décider du ravitaillement. Une réserve de foreuse inutile est aussi exclue de la demande de combustible.

Un nouvel objectif de 140 plaques a ensuite réussi de 110 à 140, des ticks 1252681 à 1258577. Il a récupéré vingt plaques déjà prêtes, puis utilisé les minerais du four 683 avec zéro nouvelle insertion de minerai. Un arbre a été récolté pour le combustible. La lecture indépendante au tick 1259404 constate 140 plaques portées, quatre en sortie, 33 minerais en entrée et 69 produits terminés par ce four. Cet essai de développement est assisté par les corrections et reprises décrites ci-dessus ; il ne qualifie aucune campagne finale.

Un essai explicite du chemin d'extraction automatique a ensuite porté le stock de 140 à 155, des ticks 1259465 à 1271660. Six photographies ont confirmé zéro minerai supplémentaire nécessaire, en tenant compte de la cuisson engagée. Aucune opération de minage, construction ou alimentation de la foreuse n'a été soumise. L'agent a récupéré un bois en stock, complétant les deux portés, puis chargé les trois dans le four. Les produits déjà prêts ont été collectés avant ce ravitaillement. Une seule connexion est restée établie pendant le trajet.

La lecture indépendante au tick 1273288 confirme 155 plaques portées, neuf en sortie, treize minerais en entrée et 89 produits terminés par le four. Ce relevé est postérieur à la fin de commande : les produits supplémentaires ne sont pas tous attribués à la durée de son exécution. Le personnage possède 250 points de vie, sans joueur connecté. Le checkpoint 1273330 est sauvegardé, son empreinte vérifiée et le serveur arrêté. Après arrêt, aucune connexion n'est établie et trois connexions isolées restent temporairement en attente de fermeture. La stabilité réseau et la reprise de stocks sont vérifiées dans ces essais ; une campagne complète reste à démontrer.
