-- Warehouse test schema for Dynamic M query parameter + DirectQuery model.
DROP TABLE IF EXISTS dbo.SalesFact;
GO
DROP TABLE IF EXISTS dbo.Region;
GO
CREATE TABLE dbo.Region (RegionName varchar(20) NOT NULL);
GO
INSERT INTO dbo.Region (RegionName) VALUES ('East'),('West'),('North'),('South');
GO
CREATE TABLE dbo.SalesFact (
    SaleId int NOT NULL,
    RegionName varchar(20) NOT NULL,
    Category varchar(30) NOT NULL,
    Product varchar(40) NOT NULL,
    Quantity int NOT NULL,
    Amount decimal(18,2) NOT NULL,
    SaleDate date NOT NULL
);
GO
INSERT INTO dbo.SalesFact (SaleId, RegionName, Category, Product, Quantity, Amount, SaleDate) VALUES
 (1 ,'East' ,'Bikes'      ,'Mountain-200'   , 3, 6749.97,'2024-01-05'),
 (2 ,'East' ,'Accessories','Helmet'         ,12,  419.88,'2024-01-11'),
 (3 ,'East' ,'Clothing'   ,'Jersey'         , 8,  439.92,'2024-02-02'),
 (4 ,'West' ,'Bikes'      ,'Road-150'       , 2, 7198.00,'2024-01-19'),
 (5 ,'West' ,'Accessories','Bottle'         ,40,  399.60,'2024-02-14'),
 (6 ,'West' ,'Clothing'   ,'Gloves'         ,15,  374.85,'2024-03-03'),
 (7 ,'North','Bikes'      ,'Touring-1000'   , 4, 9199.96,'2024-01-27'),
 (8 ,'North','Accessories','Pump'           , 9,  224.91,'2024-02-20'),
 (9 ,'North','Clothing'   ,'Shorts'         ,11,  769.89,'2024-03-15'),
 (10,'South','Bikes'      ,'Mountain-100'   , 1, 3399.99,'2024-01-09'),
 (11,'South','Accessories','Lights'         ,20,  499.80,'2024-02-25'),
 (12,'South','Clothing'   ,'Socks'          ,50,  449.50,'2024-03-21'),
 (13,'East' ,'Bikes'      ,'Road-250'       , 2, 4390.00,'2024-04-04'),
 (14,'West' ,'Bikes'      ,'Mountain-500'   , 5, 5499.95,'2024-04-10'),
 (15,'North','Accessories','Fenders'        ,18,  359.82,'2024-04-16'),
 (16,'South','Clothing'   ,'Cap'            ,30,  269.70,'2024-04-22'),
 (17,'East' ,'Accessories','Tire'           ,25,  624.75,'2024-05-02'),
 (18,'West' ,'Clothing'   ,'Vest'           , 7,  489.93,'2024-05-08'),
 (19,'North','Bikes'      ,'Road-350'       , 3, 5397.00,'2024-05-14'),
 (20,'South','Accessories','Chain'          ,14,  349.86,'2024-05-20');
GO
SELECT RegionName, COUNT(*) AS Rows, SUM(Amount) AS Total FROM dbo.SalesFact GROUP BY RegionName ORDER BY RegionName;
GO
