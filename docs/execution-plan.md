# Implémentation et qualification de l'agent Factorio

Statut : exécution autorisée par l'objectif actif de l'utilisateur. Ce plan complète la conception et remplace les mentions antérieures de suspension. La création du dépôt public MIT et les essais avec le Factorio fourni sont inclus dans l'autorisation.

## Résultat final exigé

Agent majoritairement C#, mod Lua minimal, Factorio 2.0.77 de base, modèle `glm-5.3-flash:cloud` via Ollama. Progression depuis un départ normal jusqu'au lancement constaté d'une fusée, avec pollution, évolution, expansion et attaques actives. Qualification finale sur trois graines documentées, sans intervention humaine ni apport artificiel dans les campagnes de réussite. Aucun jalon technique ne remplace ce résultat.

## Contraintes et preuves

- SOLID, DRY et KISS ; interfaces aux frontières, composants testables, pas de microservices superflus.
- Objectifs stratégiques libres du modèle, traduction C# en critères mesurables, validation stricte des appels d'outils. Aucun Lua ou placement produit par le LLM.
- Synthèse complète des implantations en C#, bilans de production, contraintes spatiales et routage ; pas de bibliothèque de gabarits prédéfinis. Les résultats de solveur distinguent solution, délai et impossibilité prouvée.
- V1 jusqu'à la fusée avec tapis et tuyaux ; trains et logistique robotique différés.
- Stocks exacts de toute l'usine propre connue. Périmètre, date, capacités, réservations, transit, fabrication engagée et débits mesurés sont distincts. La source de vérité est le moteur.
- Visibilité ennemie normale du personnage/radars ; informations historiques marquées anciennes, aucune révélation par un observateur supplémentaire.
- Identité des opérations, déduplication, préconditions revérifiées, effets partiels et preuves. Un timeout réseau donne un résultat inconnu, jamais un échec autorisant une répétition aveugle.
- Survie, défense, récupération et production ont cet ordre de priorité. Arbitre unique du personnage et préemption explicite ; défense indépendante du LLM.
- Personnage autonome sans client humain ; connexion du pilote au même personnage et déconnexion sans duplication. Bouton IA/Manuel ; reprise par observation et replanification. Intervention humaine marquée dans la campagne.
- Mort : réapparition normale, cadavre à récupérer et reconstruction dans le même monde. Ni restauration pour effacer une défaite ni équipement gratuit.
- Mémoire et journal C# persistants ; reçus et état Lua sauvegardés. Reprise et changement de monde détectés.
- Dépôt public `skyline624/factorio_llm`, MIT, documentation française, CI C# ; aucun jeu, modèle, secret, sauvegarde ou journal privé publié.
- Essais réels headless et client graphique dès le socle d'acteur. Les fixtures artificielles sont explicitement nommées et ne comptent pas comme campagnes.

## Jalons et portes de vérification

1. **Socle public et contrats** : sources réellement compilées, tests de transport, licence et documentation, contrat versionné du mod, dépôt distant vérifié.
2. **Acteur et opérations natives** : serveur sans client, marche/minage/fabrication/construction/transferts/tir, preuve des coûts et effets, interruption, mort, sauvegarde, raccordement du pilote et contrôle manuel.
3. **État complet et survie** : comptabilité des inventaires/fluides, couverture de perception, fraîcheur, chemins bloqués, attaques sous charge et indisponibilité LLM.
4. **Production et synthèse spatiale** : bilans des recettes exactes, ressources accessibles, solveur de placement et routes, installations fonctionnelles et débits soutenus.
5. **Stratégie et progression** : adaptateur cloud qualifié, mémoire, objectifs libres, prérequis scientifiques, défense et récupération, chaîne complète de la fusée.
6. **Campagnes de qualification** : trois graines, départs normaux, ennemis actifs, preuves de lancement et historique depuis le départ, mortalité/assistance/erreurs/latences/consommations consignées. Revue finale contre toutes les contraintes.

Le registre de travail local suit l'avancement ; seuls les résultats vérifiés sont reportés dans la documentation publique. Les tests et campagnes restent incomplets tant que leurs preuves réelles ne sont pas disponibles.
