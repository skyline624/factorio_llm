# Sauvegarde et reprise du monde

Depuis la racine du dépôt, avec le host compilé :

```powershell
$hostDll = 'src/Factorio.Agent.Host/bin/Release/net10.0/Factorio.Agent.Host.dll'
$sessionFile = '.runtime/campaign-.../session.json'
dotnet $hostDll stop --session $sessionFile
dotnet $hostDll resume --session $sessionFile
dotnet $hostDll connect --session $sessionFile
```

## Contrat

Le host prend le contrôle exclusif du personnage. Le mod annule l'opération active avec son reçu et ses effets partiels, arrête les commandes natives et détache le pilote avant la sauvegarde. Cette dernière étape évite qu'une déconnexion native au chargement modifie la génération enregistrée.

Le monde est brièvement mis en pause pour préparer un checkpoint cohérent. Les nouvelles opérations sont refusées jusqu'au raccordement du contrôleur. Le fichier enregistré reçoit une empreinte SHA-256, un identifiant, un tick et le périmètre de contrôle du personnage. Cette pause concerne l'arrêt du serveur ; la production et les attaques continuent normalement pendant le jeu.

La reprise exige que l'ancien serveur soit terminé. Elle vérifie le fichier et refuse un checkpoint antérieur au dernier tick, à la dernière incarnation ou à la dernière génération observés. Le moteur charge une copie distincte ; la sauvegarde source reste intacte. Avant le handshake, une observation vérifie exactement le tick, le périmètre et l'identifiant préparés.

Le nouveau contrôleur reçoit un autre identifiant de session et de nouveaux paramètres RCON locaux. Après le handshake, le mod restaure l'état de pause antérieur et incrémente la génération. Les anciennes soumissions restent dédupliquées ; une nouvelle opération portant l'ancien périmètre est refusée. Le host ne répète pas un handshake dont la réponse est ambiguë.

Les anciens profils et observations avant/après reprise restent dans le répertoire privé de la session. Le manifeste est remplacé atomiquement. `connect` prépare un nouveau profil et refuse de lancer un autre client si un joueur est connecté ou si un client géré est encore actif.

## Portée et limites

- La reprise concerne le monde, le personnage et ses reçus. Elle ne restaure pas encore le raisonnement, la mémoire d'exploration ni l'objectif stratégique du contrôleur.
- Les checkpoints anciens sans empreinte passent par une inspection native conservatrice : même monde/session, historique non régressif et aucune opération active. Cette compatibilité ne fournit pas la preuve cryptographique préalable d'un checkpoint préparé.
- Une empreinte locale détecte une modification du fichier ; elle n'est pas une signature contre un auteur capable de modifier également le journal.
- Une sauvegarde autonome après crash, une corruption et une réponse de handshake perdue nécessitent encore une réconciliation. Le manifeste conserve l'identité du processus lancé ; une observation échouée ne déclenche pas un autre lancement.
- Sur cette installation Windows, certains processus restent ouverts après le message natif de fermeture de la partie. `stop` attend jusqu'à trois minutes, puis signale explicitement que l'arrêt n'est pas confirmé. `resume` refuse tant que ce processus vit. Une récupération automatique de ce cas reste à développer.
- Ni ce mécanisme ni le watermark ne prouvent une détection universelle d'un historique restauré puis avancé au-delà de tous les compteurs connus.

## Essais réels

Sur une fixture, une marche active a été annulée avant sauvegarde. Le checkpoint a été chargé exactement au tick 3 205 ; le reçu est resté `cancelled`, sa répétition n'a produit aucun déplacement, et une nouvelle soumission avec l'ancien périmètre a été refusée par `stale_scope`.

Après correction du détachement préalable, une sauvegarde demandée avec pilote connecté a été chargée exactement au tick 17 292, génération 9 ; le handshake passe ensuite à la génération 10. Le processus précédent avait nécessité une terminaison ciblée après constat de la fermeture native de la partie et vérification du fichier : cet essai ne démontre donc pas un cycle d'exploitation sans intervention.

Dans le monde normal de développement, la reprise de l'ancienne sauvegarde a conservé les 100 plaques, le four, la recherche vapeur et l'avatar natif 17. Une fabrication interrompue par la connexion du pilote a laissé deux engrenages et un reçu d'annulation. Une nouvelle planification fondée sur les stocks a fabriqué les huit restants, laissant 80 plaques et dix engrenages. Un checkpoint préparé de cette partie a ensuite repris avec les mêmes stocks. Ces essais ne constituent pas une campagne autonome jusqu'à la fusée.
