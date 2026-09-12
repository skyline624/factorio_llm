# Ravitaillement d'une chaudière par coffre et bras

`fuel-feeder --session FILE --boiler ID --reserve 50 --ticks 3600` prépare une réserve de combustible et vérifie son transfert natif vers une chaudière pendant une fenêtre donnée. Le contrôleur fabrique les équipements manquants, collecte le combustible, construit le raccordement et démarre la chaudière avec au plus deux unités directement livrées. Après cette phase, le bras assure les transferts pendant l'observation.

## Implantation et preuves

C# utilise les positions natives de prise et de dépôt du prototype de bras, ses orientations, les collisions observées et les portées électriques. Le coffre est placé à la prise et le dépôt doit viser la chaudière. Si nécessaire, un poteau supplémentaire est calculé dans la portée de fil d'un poteau du même réseau. Les approches de construction conservent l'accès aux éléments restant à poser.

Le moteur doit ensuite confirmer le coffre comme cible de prise, la chaudière comme cible de dépôt et le raccordement électrique du bras. Un équipement existant n'est réutilisé qu'avec ces identités natives. Le stock du coffre est contrôlé avant chargement, ainsi que sa capacité d'insertion. Un coffre contenant des objets étrangers au combustible choisi est refusé.

Chaque photographie dédupliquée sépare le combustible du coffre et celui tenu par le bras. Pendant la phase sans ravitaillement par le personnage, les livraisons sont calculées par diminution de leur total, sous vérification des deux cibles du bras et de l'absence d'un autre bras prélevant dans ce coffre. Une unité encore dans la main reste en transit. Un enregistrement absent ou ambigu provoque une erreur et ne devient pas un stock nul.

## Essai dans l'économie normale, Factorio 2.0.77

Le premier essai dans la fixture de chimie a été refusé avant toute construction : l'espace autour de sa chaudière ne permettait pas de placer l'ensemble. La qualification suivante a utilisé le monde de développement normal, graine 424242, chaudière 637. Aucun objet, combustible ou déblocage n'a été accordé pour cet essai.

Le personnage a fabriqué un coffre en fer, un bras et des petits poteaux avec les matériaux disponibles ou produits. Il a posé le poteau 660 en (-33,5 ; -26,5), le coffre 661 en (-32,5 ; -27,5) et le bras 662 en (-32,5 ; -26,5). Il a ensuite collecté le bois nécessaire et chargé une réserve de 50 unités. Ces coordonnées sont le résultat du calcul C#, pas un gabarit.

La fenêtre mesurée s'étend des ticks **937151 à 940827**, soit **3676 ticks**. Le total coffre–main passe de 46 à 44 unités : **deux unités livrées pendant la fenêtre**, en plus des transferts initiaux. La production électrique est positive dans **43 relevés sur 43**. Le coffre conserve 44 unités de bois et la main est vide à la fin.

Une lecture indépendante après sauvegarde et reprise, au tick **948654**, confirme :

- bras 662 prélevant dans le coffre 661 et déposant dans la chaudière 637 ;
- bras et générateur 638 sur le réseau électrique 1 ;
- coffre contenant 44 unités de bois, chaudière contenant cinq unités et environ 1,15 MJ de combustible en cours de combustion ;
- générateur produisant 55 J au dernier tick, soit une faible charge d'environ 3,3 kW ;
- personnage à 250 points de vie, aucun joueur connecté, pollution active et mode pacifique désactivé ;
- monde non marqué fixture, zéro intervention humaine et zéro fusée rapportés par le mod.

La première tentative de lecture indépendante avait échoué sur une recherche d'entité par numéro. La vérification a ensuite retrouvé les bâtiments à leurs positions observées et exigé leurs numéros exacts avant lecture. Cette erreur de lecture n'a pas modifié les bâtiments ni servi de preuve.

Les journaux et preuves restent privés, avec la sauvegarde du monde. Le serveur a été arrêté proprement. Les fenêtres graphiques existantes ont été conservées, sans en ouvrir d'autre.

## Limites

Cet essai vérifie un approvisionnement sur réserve finie et une faible charge, pas une centrale industrielle ni un débit maximal. Le renouvellement automatique de la réserve, les tapis depuis une extraction de combustible et l'intégration de l'installation de ce ravitailleur dans la stratégie restent à réaliser. La maintenance de production existante ne doit pas être présentée comme un remplissage automatique du nouveau coffre.

Une implantation locale peut être refusée faute de place, même si une réorganisation générale de l'usine serait possible. Le plan actuel autorise un seul nouveau poteau. Les réparations après destruction et les changements d'alimentation pendant une attaque restent à qualifier. Ce monde comporte des corrections de développement antérieures ; cet essai ne compte pas comme une des trois campagnes autonomes jusqu'à la fusée.
